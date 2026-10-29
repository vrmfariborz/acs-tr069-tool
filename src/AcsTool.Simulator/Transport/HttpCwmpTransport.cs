using System.Net;
using System.Net.Http.Headers;
using AcsTool.Core.Net;
using AcsTool.Core.Sessions;
using AcsTool.Core.Settings;
using AcsTool.Simulator.Profiles;

namespace AcsTool.Simulator.Transport;

public interface ICwmpTransport : IAsyncDisposable
{
    /// <summary>POSTs one CWMP message (empty body = empty POST). Never throws for network/HTTP failures.</summary>
    Task<TransportResponse> PostAsync(ReadOnlyMemory<byte> body, string? soapAction, CancellationToken ct);
}

public sealed record TransportResponse(int? StatusCode, IReadOnlyList<KeyValuePair<string, string>> Headers,
    ReadOnlyMemory<byte> Body, UpstreamError? Error, ExchangeTimings Timings, HttpMessageCapture RequestCapture)
{
    public bool IsTransportFailure => StatusCode is null || Error is { Kind: not UpstreamErrorKind.HttpStatus };

    /// <summary>Capture limit for the response body (CaptureSettings.MaxCapturedBodyBytes of the transport); default 1 MiB.</summary>
    public int MaxCapturedBodyBytes { get; init; } = new CaptureSettings().MaxCapturedBodyBytes;
}

public interface ICwmpTransportFactory
{
    ICwmpTransport Create(CpeProfile profile, ISecretProtector secrets);
}

public sealed class HttpCwmpTransportFactory(IUpstreamHandlerFactory handlers, SettingsHolder settings, TimeProvider time) : ICwmpTransportFactory
{
    public ICwmpTransport Create(CpeProfile profile, ISecretProtector secrets)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(secrets);
        NetworkCredential? cred = null;
        if (!string.IsNullOrEmpty(profile.Username))
        {
            var pass = string.IsNullOrEmpty(profile.PasswordProtected) ? "" : secrets.Unprotect(profile.PasswordProtected);
            cred = new NetworkCredential(profile.Username, pass);
        }
        return new HttpCwmpTransport(profile, cred, handlers, settings.Current.Simulator, settings.Current.Capture, time);
    }
}

/// <summary>
/// Per-CPE HTTP transport (plan §3.7.1): one SocketsHttpHandler with its own cookie jar, credentials (Basic/Digest handled by
/// the handler), PreAuthenticate, auto-redirects (max 5) and a single connection. Timeouts and error classification are shared
/// with the proxy via <see cref="UpstreamSender"/>.
/// </summary>
public sealed class HttpCwmpTransport : ICwmpTransport
{
    private readonly Uri _url;
    private readonly HttpMessageInvoker _invoker;
    private readonly PhaseTracker _tracker = new();
    private readonly SimulatorSettings _sim;
    private readonly CaptureSettings _capture;
    private readonly TimeProvider _time;
    private readonly TimeSpan _timeout;
    private string? _lastTlsErrors;

    public HttpCwmpTransport(CpeProfile profile, NetworkCredential? credentials, IUpstreamHandlerFactory handlers,
        SimulatorSettings sim, CaptureSettings capture, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(handlers);
        _url = new Uri(profile.AcsUrl, UriKind.Absolute);
        _sim = sim;
        _capture = capture;
        _time = time;
        _timeout = TimeSpan.FromSeconds(Math.Max(1, profile.RequestTimeoutSeconds));
        Cookies = new CookieContainer();
        var handler = handlers.Create(
            new UpstreamHandlerOptions(TimeSpan.FromSeconds(Math.Min(10, Math.Max(1, profile.RequestTimeoutSeconds))), profile.Tls,
                UseCookies: true, Cookies, credentials, AllowAutoRedirect: true, UseSystemProxy: false)
            { PreAuthenticate = credentials is not null, MaxAutomaticRedirections = 5 },
            () => _tracker,
            tls => _lastTlsErrors = tls.PolicyErrors);
        _invoker = new HttpMessageInvoker(handler, disposeHandler: true);
    }

    public CookieContainer Cookies { get; }

    public async Task<TransportResponse> PostAsync(ReadOnlyMemory<byte> body, string? soapAction, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _url)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ReadOnlyMemoryContent(body),
        };
        req.Headers.TryAddWithoutValidation("User-Agent", _sim.UserAgent);
        if (body.Length > 0)
        {
            req.Content.Headers.TryAddWithoutValidation("Content-Type", "text/xml; charset=\"utf-8\"");
            if (soapAction is not null)
            {
                req.Headers.TryAddWithoutValidation("SOAPAction", soapAction);
            }
        }
        req.Content.Headers.ContentLength = body.Length;

        var r = await UpstreamSender.SendAsync(_invoker, req, _tracker, _timeout, 16 * 1024 * 1024, _time, ct, () => _lastTlsErrors)
            .ConfigureAwait(false);
        var reqHeaders = CaptureRequestHeaders(req);
        var capture = HttpMessageCapture.From("POST", req.RequestUri?.ToString() ?? _url.ToString(), null, reqHeaders, body.Span, _capture.MaxCapturedBodyBytes);
        return new TransportResponse(r.StatusCode, r.Headers, r.Body, r.Error, r.Timings, capture) { MaxCapturedBodyBytes = _capture.MaxCapturedBodyBytes };
    }

    private List<KeyValuePair<string, string>> CaptureRequestHeaders(HttpRequestMessage req)
    {
        var list = new List<KeyValuePair<string, string>> { new("Host", _url.Authority) };
        foreach (var h in req.Headers)
        {
            list.AddRange(h.Value.Select(v => new KeyValuePair<string, string>(h.Key, v)));
        }
        if (req.Content is not null)
        {
            foreach (var h in req.Content.Headers)
            {
                list.AddRange(h.Value.Select(v => new KeyValuePair<string, string>(h.Key, v)));
            }
        }
        var cookie = Cookies.GetCookieHeader(_url);
        if (cookie.Length > 0 && !list.Any(h => h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)))
        {
            list.Add(new("Cookie", cookie));
        }
        return list;
    }

    public ValueTask DisposeAsync()
    {
        _invoker.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal static class SoapActionHeader
{
    public static string? For(SoapActionMode mode, string methodName, string cwmpNamespace) => mode switch
    {
        SoapActionMode.Omit => null,
        SoapActionMode.MethodName => $"\"{cwmpNamespace}#{methodName}\"",
        _ => "\"\"",
    };
}
