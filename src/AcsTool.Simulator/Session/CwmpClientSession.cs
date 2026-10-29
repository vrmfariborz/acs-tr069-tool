using AcsTool.Core.Building;
using AcsTool.Core.Cwmp;
using AcsTool.Core.Model;
using AcsTool.Core.Net;
using AcsTool.Core.Parsing;
using AcsTool.Core.Sessions;
using AcsTool.Core.Settings;
using AcsTool.Simulator.Parameters;
using AcsTool.Simulator.Profiles;
using AcsTool.Simulator.Rpc;
using AcsTool.Simulator.Transport;
using Microsoft.Extensions.Logging;

namespace AcsTool.Simulator.Session;

public sealed record ClientSessionContext(CpeProfile Profile, DeviceIdStruct DeviceId, ICwmpTransport Transport, ParameterStore Store,
    IReadOnlyList<EventStruct> Events, IReadOnlyList<ParameterValue> InformParameters, uint RetryCount, Func<string> NextId,
    IReadOnlyList<TransferCompleteMessage> PendingCpeRequests, ISessionSink Sink, RpcDispatcher Rpc, ICwmpParser Parser,
    IMessageSummarizer Summarizer, ICwmpMessageBuilder Builder, IInformBuilder Inform, SimulatorSettings Settings,
    TimeProvider Time, ILogger Log)
{
    public int MaxMessages { get; init; } = 1000;

    public TimeSpan HardLimit { get; init; } = TimeSpan.FromMinutes(10);

    public string ClientEndpoint { get; init; } = "simulator";
}

public sealed record ClientSessionResult(SessionOutcome Outcome, bool InformAccepted, IReadOnlyList<FollowUp> FollowUps, UpstreamError? Error)
{
    public Guid SessionId { get; init; }

    public int DeliveredCpeRequests { get; init; }

    public string? Note { get; init; }
}

/// <summary>One CWMP session from the CPE side (plan §3.7.2). Unit-testable with a fake <see cref="ICwmpTransport"/>.</summary>
public sealed class CwmpClientSession
{
    private ClientSessionContext _ctx = null!;
    private Guid _sid;
    private int _messages;

    public async Task<ClientSessionResult> RunAsync(ClientSessionContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        _ctx = ctx;
        _messages = 0;
        var version = ctx.Profile.Version;
        _sid = ctx.Sink.StartSession(new SessionStart(SessionSource.Simulator, ctx.ClientEndpoint, ctx.Profile.AcsUrl, ctx.DeviceId,
            ctx.Events, version, null));
        using var hard = new CancellationTokenSource(ctx.HardLimit, ctx.Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, hard.Token);
        var token = linked.Token;
        var followUps = new List<FollowUp>();
        var informAccepted = false;
        var delivered = 0;
        try
        {
            // 1. Inform
            var inform = ctx.Inform.Create(ctx.DeviceId, ctx.Events, ctx.InformParameters, ctx.RetryCount, ctx.Time.GetLocalNow(), ctx.Profile.MaxEnvelopes);
            var informId = ctx.NextId();
            var (r, parsed, cap) = await ExchangeAsync(ctx.Builder.Build(inform, version, new CwmpHeader(informId)), "Inform", token).ConfigureAwait(false);
            if (cap is { } c1)
            {
                return End(c1, informAccepted, followUps, delivered);
            }
            // A SOAP Fault answering the Inform is checked before the HTTP status, like the proxy (CpeSessionTracker.EvaluateEnd):
            // SOAP 1.1 faults usually come with HTTP 500. 401/407 challenges keep their status classification (doc review a).
            if (parsed.Kind == CwmpMessageKind.Fault && r.StatusCode is not (401 or 407))
            {
                var f = (CwmpFault?)parsed.Message;
                return End(SessionOutcome.AcsFault, null, informAccepted, followUps, delivered, $"ACS fault {f?.FaultCode} {f?.FaultString}");
            }
            if (r.StatusCode == 401)
            {
                return End(SessionOutcome.AuthFailed, r.Error, informAccepted, followUps, delivered, "ACS rejected the credentials (401)");
            }
            if (r.StatusCode is not (>= 200 and < 300))
            {
                return End(SessionOutcome.HttpError, r.Error, informAccepted, followUps, delivered);
            }
            if (parsed.Kind != CwmpMessageKind.InformResponse)
            {
                return End(SessionOutcome.ProtocolError, null, informAccepted, followUps, delivered,
                    parsed.IsEmpty ? "Empty response to the Inform (InformResponse expected)" : $"Expected InformResponse, got {parsed.Kind}");
            }
            informAccepted = true;
            if (parsed.Envelope!.Header.Id != informId)
            {
                Note($"InformResponse cwmp:ID '{parsed.Envelope.Header.Id}' does not match the Inform ID '{informId}'");
            }
            var hold = parsed.Envelope.Header.HoldRequests == true;

            // 2. CPE requests (TransferComplete), unless the ACS holds requests
            foreach (var tc in ctx.PendingCpeRequests)
            {
                if (hold)
                {
                    Note("HoldRequests=true: pending TransferComplete kept for a later session");
                    break;
                }
                (r, parsed, cap) = await ExchangeAsync(ctx.Builder.Build(tc, version, new CwmpHeader(ctx.NextId())), "TransferComplete", token).ConfigureAwait(false);
                if (cap is { } c2)
                {
                    return End(c2, informAccepted, followUps, delivered);
                }
                if (parsed.Kind == CwmpMessageKind.TransferCompleteResponse)
                {
                    delivered++;
                }
                else
                {
                    Note($"TransferComplete not acknowledged ({parsed.Kind}); kept pending");
                }
                hold = parsed.Envelope?.Header.HoldRequests == true;
            }

            // 3. Empty POST, then answer ACS requests until the ACS sends an empty response
            var body = ReadOnlyMemory<byte>.Empty;
            var method = "empty";
            while (true)
            {
                (r, parsed, cap) = await ExchangeAsync(body, method, token).ConfigureAwait(false);
                if (cap is { } c3)
                {
                    return End(c3, informAccepted, followUps, delivered);
                }
                if (r.StatusCode == 401)
                {
                    return End(SessionOutcome.AuthFailed, r.Error, informAccepted, followUps, delivered);
                }
                if (r.StatusCode is not (>= 200 and < 300))
                {
                    return End(SessionOutcome.HttpError, r.Error, informAccepted, followUps, delivered);
                }
                if (parsed.IsEmpty)
                {
                    return End(SessionOutcome.Completed, null, informAccepted, followUps, delivered);
                }
                if (parsed.Kind is CwmpMessageKind.NotXml or CwmpMessageKind.NotSoap || parsed.Envelope is null)
                {
                    return End(SessionOutcome.ProtocolError, null, informAccepted, followUps, delivered, $"Unparseable ACS message ({parsed.Kind})");
                }
                var env = parsed.Envelope;
                if (env.Header.HoldRequests is { } h)
                {
                    hold = h;
                }
                if (env.Kind.IsResponse() || env.Kind == CwmpMessageKind.Fault || env.Kind == CwmpMessageKind.Inform)
                {
                    Note($"ACS sent {env.Kind}, which is not a request; sending an empty POST");
                    body = ReadOnlyMemory<byte>.Empty;
                    method = "empty";
                    continue;
                }
                var result = ctx.Rpc.Dispatch(env, new RpcContext(ctx.Store, ctx.Profile, ctx.Log));
                if (result.Hang is { } hang)
                {
                    await Task.Delay(hang, ctx.Time, token).ConfigureAwait(false);
                    return End(SessionOutcome.Canceled, null, informAccepted, followUps, delivered, "simulated hang");
                }
                if (result.FollowUp is { } fu)
                {
                    followUps.Add(fu);
                }
                var header = new CwmpHeader(env.Header.Id);
                CwmpMessage reply = result.Fault ?? result.Response ?? CwmpMessageBuilder.CreateFault(FaultCodes.InternalError);
                body = ctx.Builder.Build(reply, env.Version == CwmpVersion.Unknown ? version : env.Version, header);
                method = reply is CwmpFault ? "Fault" : reply.KindOf().ToString();
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            var timedOut = hard.IsCancellationRequested && !ct.IsCancellationRequested;
            return End(SessionOutcome.Canceled, null, informAccepted, followUps, delivered,
                timedOut ? "Session hard limit (10 min) reached" : "Session canceled");
        }
#pragma warning disable CA1031 // an internal failure must end the stored session instead of leaving it InProgress (review m5)
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            ctx.Log.LogError(3201, ex, "Simulated session for {Device} failed", ctx.DeviceId.Key);
            return End(SessionOutcome.ProtocolError, null, informAccepted, followUps, delivered, $"Internal error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<(TransportResponse Response, ParseResult Parsed, (SessionOutcome Outcome, UpstreamError? Error, string? Note)? Fail)>
        ExchangeAsync(ReadOnlyMemory<byte> body, string method, CancellationToken ct)
    {
        if (++_messages > _ctx.MaxMessages)
        {
            var dummy = new TransportResponse(null, [], default, null, new ExchangeTimings(_ctx.Time.GetUtcNow()),
                HttpMessageCapture.From("POST", _ctx.Profile.AcsUrl, null, [], default, 0));
            return (dummy, ParseResult.EmptyBody, (SessionOutcome.ProtocolError, null, $"More than {_ctx.MaxMessages} messages in one session"));
        }
        var reqParsed = body.IsEmpty ? ParseResult.EmptyBody : _ctx.Parser.Parse(body.Span);
        var reqSummary = _ctx.Summarizer.Summarize(reqParsed);
        var soap = body.IsEmpty ? null : SoapActionHeader.For(_ctx.Settings.SoapAction, method, CwmpNamespaces.For(_ctx.Profile.Version));
        var r = await _ctx.Transport.PostAsync(body, soap, ct).ConfigureAwait(false);
        // The exchange is recorded once the transport returns so the request capture includes the final headers (auth, cookies).
        var seq = _ctx.Sink.AddExchange(_sid, r.RequestCapture, reqSummary, r.Timings.Started);
        ParseResult parsed = ParseResult.EmptyBody;
        HttpMessageCapture? respCap = null;
        MessageSummary? respSummary = null;
        if (r.StatusCode is { } status)
        {
            parsed = r.Body.IsEmpty ? ParseResult.EmptyBody : _ctx.Parser.Parse(r.Body.Span, HeaderUtil.Get(r.Headers, "Content-Type"));
            respSummary = _ctx.Summarizer.Summarize(parsed);
            respCap = HttpMessageCapture.From(null, _ctx.Profile.AcsUrl, status, r.Headers, r.Body.Span, r.MaxCapturedBodyBytes);
        }
        _ctx.Sink.CompleteExchange(_sid, seq, respCap, respSummary, r.Error, r.Timings);
        ct.ThrowIfCancellationRequested();
        if (r.IsTransportFailure)
        {
            return (r, parsed, (SessionOutcome.UpstreamError, r.Error, null));
        }
        return (r, parsed, null);
    }

    private void Note(string note) => _ctx.Sink.UpdateSession(_sid, s => s with { Notes = [.. s.Notes, note] });

    private ClientSessionResult End((SessionOutcome Outcome, UpstreamError? Error, string? Note) f, bool accepted, List<FollowUp> followUps, int delivered) =>
        End(f.Outcome, f.Error, accepted, followUps, delivered, f.Note);

    private ClientSessionResult End(SessionOutcome outcome, UpstreamError? error, bool accepted, List<FollowUp> followUps, int delivered, string? note = null)
    {
        if (note is not null)
        {
            Note(note);
        }
        _ctx.Sink.EndSession(_sid, outcome);
        _ctx.Log.LogInformation(3201, "Simulator session {Session} for {Device} ended: {Outcome}", _sid, _ctx.DeviceId.Key, outcome);
        return new ClientSessionResult(outcome, accepted, followUps, error) { SessionId = _sid, DeliveredCpeRequests = delivered, Note = note };
    }
}
