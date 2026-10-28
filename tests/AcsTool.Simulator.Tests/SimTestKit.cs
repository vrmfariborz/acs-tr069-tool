using AcsTool.Core.Building;
using AcsTool.Core.Cwmp;
using AcsTool.Core.Model;
using AcsTool.Core.Net;
using AcsTool.Core.Parsing;
using AcsTool.Core.Sessions;
using AcsTool.Core.Settings;
using AcsTool.Core.Util;
using AcsTool.Simulator.Engine;
using AcsTool.Simulator.Parameters;
using AcsTool.Simulator.Profiles;
using AcsTool.Simulator.Rpc;
using AcsTool.Simulator.Session;
using AcsTool.Simulator.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace AcsTool.Simulator.Tests;

/// <summary>Scripted in-memory transport: each POST is answered by the next responder.</summary>
public sealed class FakeTransport : ICwmpTransport
{
    private readonly Queue<Func<ParseResult, TransportResponse>> _script = new();
    private static readonly CwmpParser Parser = new();

    public List<(byte[] Body, string? SoapAction, ParseResult Parsed)> Sent { get; } = [];

    public Func<ParseResult, TransportResponse>? Fallback { get; set; }

    public FakeTransport Then(Func<ParseResult, TransportResponse> f)
    {
        _script.Enqueue(f);
        return this;
    }

    public FakeTransport ThenMessage(Func<ParseResult, CwmpMessage> f, Func<ParseResult, CwmpHeader>? header = null) =>
        Then(p => Ok(Kit.Builder.Build(f(p), CwmpVersion.V1_0, header?.Invoke(p) ?? new CwmpHeader(p.Envelope?.Header.Id))));

    public FakeTransport ThenEmpty(int status = 204) => Then(_ => Ok([], status));

    public static TransportResponse Ok(byte[] body, int status = 200) =>
        new(status, [], body, ErrorClassifier.FromStatus(status), new ExchangeTimings(DateTimeOffset.UtcNow), HttpMessageCapture.From("POST", "http://acs", null, [], default, 0));

    public static TransportResponse Error(UpstreamErrorKind kind, RequestPhase phase) =>
        new(null, [], default, new UpstreamError(kind, phase, kind.ToString()), new ExchangeTimings(DateTimeOffset.UtcNow), HttpMessageCapture.From("POST", "http://acs", null, [], default, 0));

    public Task<TransportResponse> PostAsync(ReadOnlyMemory<byte> body, string? soapAction, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var parsed = body.IsEmpty ? ParseResult.EmptyBody : Parser.Parse(body.Span);
        Sent.Add((body.ToArray(), soapAction, parsed));
        var f = _script.Count > 0 ? _script.Dequeue() : Fallback ?? throw new InvalidOperationException("FakeTransport script exhausted");
        return Task.FromResult(f(parsed));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Records sink calls in order, delegating to a real store.</summary>
public sealed class RecordingSink(InMemorySessionStore inner) : ISessionSink
{
    public List<string> Calls { get; } = [];

    public Guid StartSession(SessionStart start)
    {
        Calls.Add("Start");
        return inner.StartSession(start);
    }

    public int AddExchange(Guid sessionId, HttpMessageCapture request, MessageSummary summary, DateTimeOffset started)
    {
        Calls.Add("Add:" + summary.Kind);
        return inner.AddExchange(sessionId, request, summary, started);
    }

    public void CompleteExchange(Guid sessionId, int seq, HttpMessageCapture? response, MessageSummary? summary, UpstreamError? error,
        ExchangeTimings timings, IReadOnlyList<string>? notes = null)
    {
        Calls.Add("Complete:" + (summary?.Kind.ToString() ?? "none"));
        inner.CompleteExchange(sessionId, seq, response, summary, error, timings, notes);
    }

    public void UpdateSession(Guid sessionId, Func<SessionSnapshot, SessionSnapshot> patch) => inner.UpdateSession(sessionId, patch);

    public void EndSession(Guid sessionId, SessionOutcome outcome)
    {
        Calls.Add("End:" + outcome);
        inner.EndSession(sessionId, outcome);
    }
}

public static class Kit
{
    public static readonly CwmpMessageBuilder Builder = new();

    public static InMemorySessionStore NewStore(TimeProvider? time = null) => new(new CaptureSettings(), time ?? TimeProvider.System);

    public static CpeProfile Profile(string acsUrl = "http://127.0.0.1:1/acs", Func<CpeProfile, CpeProfile>? configure = null)
    {
        var p = DefaultProfiles.Tr098() with { AcsUrl = acsUrl };
        return configure?.Invoke(p) ?? p;
    }

    public static ClientSessionContext Context(ICwmpTransport transport, ISessionSink sink, CpeProfile? profile = null,
        IReadOnlyList<TransferCompleteMessage>? pending = null, TimeProvider? time = null, ParameterStore? store = null)
    {
        profile ??= Profile();
        var n = 0;
        store ??= new ParameterStore(profile.Parameters);
        return new ClientSessionContext(profile, profile.DeviceId, transport, store, [new EventStruct(EventCodes.Boot)],
            [new ParameterValue("InternetGatewayDevice.DeviceInfo.SoftwareVersion", "1.0.0")], 0, () => (++n).ToString(System.Globalization.CultureInfo.InvariantCulture),
            pending ?? [], sink, RpcDispatcher.CreateDefault(), new CwmpParser(), new MessageSummarizer(), Builder, new InformBuilder(),
            new SimulatorSettings(), time ?? TimeProvider.System, NullLogger.Instance);
    }

    public static CpeServices Services(ISessionSink sink, SettingsHolder? settings = null, TimeProvider? time = null) =>
        new(new HttpCwmpTransportFactory(new UpstreamHandlerFactory(), settings ?? new SettingsHolder(), time ?? TimeProvider.System),
            RpcDispatcher.CreateDefault(), new CwmpParser(), new MessageSummarizer(), Builder, new InformBuilder(), sink,
            new PlainSecretProtector(), time ?? TimeProvider.System, new SeededRandom(1), NullLoggerFactory.Instance);

    public static SimulationEngine Engine(ISessionSink sink) => new(Services(sink), new SettingsHolder(), NullLogger<SimulationEngine>.Instance);

    public static CwmpEnvelope Envelope(CwmpMessage m, string id = "acs-1")
    {
        var parsed = new CwmpParser().Parse(Builder.Build(m, CwmpVersion.V1_0, new CwmpHeader(id)));
        return parsed.Envelope!;
    }
}
