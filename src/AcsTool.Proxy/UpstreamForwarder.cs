using System.Net;
using System.Net.Http;
using AcsTool.Core.Net;
using AcsTool.Core.Settings;

namespace AcsTool.Proxy;

public interface IUpstreamForwarder
{
    Task<ForwardResult> ForwardAsync(ProxySession session, IncomingRequest request, CancellationToken ct);
}

/// <summary>Relays one request over the session's own upstream connection (plan §3.6.2 step 7).</summary>
public sealed class UpstreamForwarder(ProxySettings settings, HeaderRewriter rewriter, TimeProvider time) : IUpstreamForwarder
{
    private static readonly HashSet<string> BodylessMethods = new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS", "DELETE", "TRACE" };

    public async Task<ForwardResult> ForwardAsync(ProxySession session, IncomingRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        var uri = BuildUpstreamUri(settings.UpstreamUrl, settings.PathMode, settings.FixedUpstreamPath, request.PathAndQuery);
        using var msg = new HttpRequestMessage(new HttpMethod(request.Method), uri)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        // Always attach content for body-carrying methods so an empty POST goes out with Content-Length: 0 (FR-P-05).
        if (!BodylessMethods.Contains(request.Method) || request.Body.Length > 0)
        {
            msg.Content = new ReadOnlyMemoryContent(request.Body);
        }
        foreach (var (name, value) in rewriter.ForUpstreamRequest(request, uri))
        {
            if (HeaderUtil.ContentHeaders.Contains(name))
            {
                msg.Content?.Headers.TryAddWithoutValidation(name, value);
            }
            else
            {
                msg.Headers.TryAddWithoutValidation(name, value);
            }
        }
        if (settings.HostHeader == HostHeaderMode.Preserve && request.Header("Host") is { } host)
        {
            msg.Headers.Host = host;
        }
        var r = await UpstreamSender.SendAsync(session.Upstream, msg, session.Tracker,
            TimeSpan.FromSeconds(Math.Max(1, settings.RequestTimeoutSeconds)), settings.MaxRequestBodyBytes, time, ct,
            () => session.LastTlsPolicyErrors).ConfigureAwait(false);
        return new ForwardResult(uri, r.StatusCode, r.Headers, r.Body, r.Error, r.Timings);
    }

    /// <summary>
    /// PassThrough: upstream base path (trailing '/' trimmed) + incoming path + query. Fixed: the fixed path (or the base URL's own path).
    /// Percent-encoding of the incoming target is preserved.
    /// </summary>
    public static Uri BuildUpstreamUri(string upstreamBase, PathMode mode, string? fixedPath, string pathAndQuery)
    {
        var b = new Uri(upstreamBase, UriKind.Absolute);
        var authority = $"{b.Scheme}://{b.Authority}";
        if (mode == PathMode.Fixed)
        {
            var p = string.IsNullOrEmpty(fixedPath) ? b.PathAndQuery : (fixedPath.StartsWith('/') ? fixedPath : "/" + fixedPath);
            return new Uri(authority + p, UriKind.Absolute);
        }
        var target = pathAndQuery;
        if (Uri.TryCreate(target, UriKind.Absolute, out var abs) && (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps))
        {
            target = abs.PathAndQuery; // absolute-form request-target
        }
        if (!target.StartsWith('/'))
        {
            target = "/" + target;
        }
        var basePath = b.AbsolutePath.TrimEnd('/');
        return new Uri(authority + basePath + target, UriKind.Absolute);
    }
}
