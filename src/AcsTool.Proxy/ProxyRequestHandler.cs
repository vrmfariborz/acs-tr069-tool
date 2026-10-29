using System.Buffers;
using AcsTool.Core.Model;
using AcsTool.Core.Net;
using AcsTool.Core.Parsing;
using AcsTool.Core.Sessions;
using AcsTool.Core.Settings;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace AcsTool.Proxy;

/// <summary>The terminal middleware: capture → parse → correlate → forward → record → relay (plan §3.6.2).</summary>
public sealed class ProxyRequestHandler
{
    private readonly ProxySettings _settings;
    private readonly CaptureSettings _capture;
    private readonly ICwmpParser _parser;
    private readonly IMessageSummarizer _summarizer;
    private readonly CpeSessionTracker _tracker;
    private readonly IUpstreamForwarder _forwarder;
    private readonly ISessionSink _sink;
    private readonly HeaderRewriter _rewriter;
    private readonly TimeProvider _time;
    private readonly ILogger<ProxyRequestHandler> _log;
    private readonly bool _upstreamHttps;

    public ProxyRequestHandler(ProxySettings settings, CaptureSettings capture, ICwmpParser parser, IMessageSummarizer summarizer,
        CpeSessionTracker tracker, IUpstreamForwarder forwarder, ISessionSink sink, HeaderRewriter rewriter, TimeProvider time,
        ILogger<ProxyRequestHandler> log)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _summarizer = summarizer ?? throw new ArgumentNullException(nameof(summarizer));
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _forwarder = forwarder ?? throw new ArgumentNullException(nameof(forwarder));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _rewriter = rewriter ?? throw new ArgumentNullException(nameof(rewriter));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _upstreamHttps = Uri.TryCreate(settings.UpstreamUrl, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;
    }

    public async Task HandleAsync(HttpContext ctx, CancellationToken serverStopping)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var started = _time.GetUtcNow();
        var ts = _time.GetTimestamp();
        IncomingRequest? req = null;
        ProxySession? session = null;
        try
        {
            var (body, tooLarge) = await ReadBodyAsync(ctx.Request.Body, _settings.MaxRequestBodyBytes, ctx.RequestAborted).ConfigureAwait(false);
            req = Snapshot(ctx, body);
            if (tooLarge)
            {
                await RejectTooLargeAsync(ctx, req, started).ConfigureAwait(false);
                return;
            }

            var parsed = _parser.Parse(req.Body.Span, req.Header("Content-Type"));
            var summary = _summarizer.Summarize(parsed);
            session = _tracker.Resolve(req, parsed, summary);

            await session.Gate.WaitAsync(ctx.RequestAborted).ConfigureAwait(false);
            try
            {
                var reqCap = HttpMessageCapture.From(req.Method, req.PathAndQuery, null, req.Headers, req.Body.Span, _capture.MaxCapturedBodyBytes);
                var seq = _sink.AddExchange(session.SessionId, reqCap, summary, started);

                var result = await _forwarder.ForwardAsync(session, req, serverStopping).ConfigureAwait(false);

                var notes = new List<string>();
                ParseResult? respParsed = null;
                MessageSummary? respSummary = null;
                HttpMessageCapture? respCap = null;
                if (result.StatusCode is { } status)
                {
                    respParsed = _parser.Parse(result.Body.Span, HeaderUtil.Get(result.Headers, "Content-Type"));
                    respSummary = _summarizer.Summarize(respParsed);
                    respCap = HttpMessageCapture.From(null, result.UpstreamUri.ToString(), status, result.Headers, result.Body.Span, _capture.MaxCapturedBodyBytes);
                }
                var downstreamHeaders = result.IsFailure ? [] : _rewriter.ForDownstreamResponse(result, req.IsHttps, _upstreamHttps, notes, req.PathAndQuery).ToList();
                var outcome = CpeSessionTracker.EvaluateEnd(session, parsed.Kind, result, respParsed?.Kind, notes);
                var timings = result.Timings with { Total = _time.GetElapsedTime(ts) };
                _sink.CompleteExchange(session.SessionId, seq, respCap, respSummary, result.Error, timings, notes);
                if (parsed.Kind == CwmpMessageKind.Inform && summary.DeviceId is { } dev && session.DeviceKey is null)
                {
                    session.DeviceKey = dev.Key;
                }
                _tracker.ObserveResponse(session, result, parsed.Kind, parsed.IsEmpty);
                if (outcome is { } o)
                {
                    await _tracker.EndAsync(session, o).ConfigureAwait(false);
                }

                await RelayAsync(ctx, session, result, downstreamHeaders).ConfigureAwait(false);
            }
            finally
            {
                session.Gate.Release();
            }
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            if (session is not null)
            {
                AddSessionNote(session, "CPE disconnected before the exchange completed");
            }
        }
#pragma warning disable CA1031 // NFR-07: the listener must never crash; log, record, answer 500
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.LogError(1900, ex, "Unhandled error while proxying {Method} {Path} from {Client}", ctx.Request.Method, ctx.Request.Path, req?.ClientEndpoint);
            if (session is not null)
            {
                AddSessionNote(session, $"Proxy internal error: {ex.GetType().Name}: {ex.Message}");
            }
            if (!ctx.Response.HasStarted)
            {
                ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                ctx.Response.ContentLength = 0;
            }
        }
    }

    private async Task RelayAsync(HttpContext ctx, ProxySession session, ForwardResult result, List<KeyValuePair<string, string>> headers)
    {
        try
        {
            if (result.IsFailure)
            {
                if (_settings.FailureMode == FailureMode.Abort)
                {
                    ctx.Abort();
                    return;
                }
                ctx.Response.StatusCode = result.Error?.Kind == UpstreamErrorKind.Timeout ? StatusCodes.Status504GatewayTimeout : StatusCodes.Status502BadGateway;
                ctx.Response.Headers.Connection = "close";
                ctx.Response.ContentLength = 0;
                return;
            }
            var status = result.StatusCode!.Value;
            ctx.Response.StatusCode = status;
            foreach (var (k, v) in headers)
            {
                ctx.Response.Headers.Append(k, v);
            }
            if (status is 204 or 304 || (status >= 100 && status < 200))
            {
                return;
            }
            ctx.Response.ContentLength = result.Body.Length;
            if (result.Body.Length > 0)
            {
                await ctx.Response.Body.WriteAsync(result.Body, ctx.RequestAborted).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ConnectionResetException or ObjectDisposedException)
        {
            AddSessionNote(session, "CPE disconnected before response relayed");
        }
    }

    private async Task RejectTooLargeAsync(HttpContext ctx, IncomingRequest req, DateTimeOffset started)
    {
        var orphan = _tracker.CreateOrphan(req, "Request body too large");
        var cap = HttpMessageCapture.From(req.Method, req.PathAndQuery, null, req.Headers, ReadOnlySpan<byte>.Empty, 0);
        var seq = _sink.AddExchange(orphan.SessionId, cap, _summarizer.Summarize(ParseResult.EmptyBody), started);
        _sink.CompleteExchange(orphan.SessionId, seq, null, null,
            new UpstreamError(UpstreamErrorKind.BodyTooLarge, RequestPhase.Dns, $"Request body exceeds {_settings.MaxRequestBodyBytes} bytes; not forwarded"),
            new ExchangeTimings(started, Total: _time.GetUtcNow() - started));
        await _tracker.EndAsync(orphan, SessionOutcome.ProtocolError).ConfigureAwait(false);
        ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        ctx.Response.ContentLength = 0;
    }

    private void AddSessionNote(ProxySession session, string note) =>
        _sink.UpdateSession(session.SessionId, s => s.Notes.Contains(note) ? s : s with { Notes = [.. s.Notes, note] });

    private static IncomingRequest Snapshot(HttpContext ctx, byte[] body)
    {
        var headers = new List<KeyValuePair<string, string>>(ctx.Request.Headers.Count);
        foreach (var h in ctx.Request.Headers)
        {
            foreach (var v in h.Value)
            {
                headers.Add(new(h.Key, v ?? string.Empty));
            }
        }
        var target = ctx.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (string.IsNullOrEmpty(target))
        {
            target = (ctx.Request.PathBase + ctx.Request.Path + ctx.Request.QueryString).ToString();
        }
        var remote = ctx.Connection.RemoteIpAddress;
        if (remote is { IsIPv4MappedToIPv6: true })
        {
            remote = remote.MapToIPv4();
        }
        return new IncomingRequest(ctx.Request.Method, target, headers, body, remote?.ToString() ?? "unknown",
            ctx.Connection.RemotePort, ctx.Connection.Id, ctx.Request.IsHttps);
    }

    private static async Task<(byte[] Body, bool TooLarge)> ReadBodyAsync(Stream body, int max, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            var tooLarge = false;
            int read;
            while ((read = await body.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
            {
                if (ms.Length + read > max)
                {
                    tooLarge = true; // keep draining so the CPE gets a clean 413
                    continue;
                }
                ms.Write(buffer, 0, read);
            }
            return (tooLarge ? [] : ms.ToArray(), tooLarge);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
