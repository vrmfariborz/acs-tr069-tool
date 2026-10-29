using AcsTool.Core.Net;
using AcsTool.Core.Settings;

namespace AcsTool.Proxy;

/// <summary>The only place headers are changed (FR-P-03 / FR-P-12). Every rewrite of a response adds a note.</summary>
public sealed class HeaderRewriter(ProxySettings settings)
{
    private static readonly HashSet<string> NeverForwardUpstream = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Expect",
    };

    private static readonly HashSet<string> NeverRelayDownstream = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Length",
    };

    public IEnumerable<KeyValuePair<string, string>> ForUpstreamRequest(IncomingRequest req, Uri upstream)
    {
        ArgumentNullException.ThrowIfNull(req);
        foreach (var h in HeaderUtil.EndToEnd(req.Headers))
        {
            if (NeverForwardUpstream.Contains(h.Key))
            {
                continue;
            }
            if (settings.CookieMode == CookieMode.ManagedPerCpe && h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (settings.AddXForwardedFor && h.Key.Equals("X-Forwarded-For", StringComparison.OrdinalIgnoreCase))
            {
                continue; // replaced below
            }
            yield return h;
        }
        if (settings.AddXForwardedFor)
        {
            var prior = req.Header("X-Forwarded-For");
            yield return new("X-Forwarded-For", string.IsNullOrEmpty(prior) ? req.RemoteIp : $"{prior}, {req.RemoteIp}");
        }
    }

    /// <param name="cpePath">The CPE's request path; when given, a cookie Path the CPE would not match is rewritten to "/" (FR-P-12).</param>
    public IEnumerable<KeyValuePair<string, string>> ForDownstreamResponse(ForwardResult r, bool downstreamHttps, bool upstreamHttps, IList<string> notes,
        string? cpePath = null)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(notes);
        var result = new List<KeyValuePair<string, string>>();
        foreach (var h in HeaderUtil.EndToEnd(r.Headers))
        {
            if (NeverRelayDownstream.Contains(h.Key))
            {
                continue;
            }
            if (h.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
            {
                if (settings.CookieMode == CookieMode.ManagedPerCpe)
                {
                    AddNote(notes, "Set-Cookie consumed by proxy (ManagedPerCpe)");
                    continue;
                }
                result.Add(new(h.Key, RewriteSetCookie(h.Value, downstreamHttps, upstreamHttps, notes, cpePath)));
                continue;
            }
            if (h.Key.Equals("Location", StringComparison.OrdinalIgnoreCase) && r.StatusCode is >= 300 and < 400)
            {
                AddNote(notes, $"3xx Location points to {h.Value} (not rewritten; the CPE may bypass the proxy)");
            }
            result.Add(h);
        }
        return result;
    }

    internal string RewriteSetCookie(string value, bool downstreamHttps, bool upstreamHttps, IList<string> notes, string? cpePath = null)
    {
        var parts = value.Split(';').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        if (parts.Count == 0)
        {
            return value;
        }
        var kept = new List<string> { parts[0] };
        var changed = false;
        foreach (var attr in parts.Skip(1))
        {
            var name = attr.Split('=', 2)[0].Trim();
            if (settings.StripCookieDomain && name.Equals("Domain", StringComparison.OrdinalIgnoreCase))
            {
                AddNote(notes, "Set-Cookie Domain stripped");
                continue;
            }
            if (settings.StripCookieSecureOnHttp && !downstreamHttps && upstreamHttps && name.Equals("Secure", StringComparison.OrdinalIgnoreCase))
            {
                AddNote(notes, "Set-Cookie Secure stripped (CPE leg is HTTP)");
                continue;
            }
            if (cpePath is not null && name.Equals("Path", StringComparison.OrdinalIgnoreCase))
            {
                var cookiePath = attr.Split('=', 2) is [_, var v] ? v.Trim() : "";
                if (cookiePath.Length > 0 && !PathMatches(cpePath, cookiePath))
                {
                    // The upstream path differs (base path / PathMode=Fixed): the CPE would never send this cookie back to the proxy.
                    AddNote(notes, $"Set-Cookie Path {cookiePath} rewritten to / (CPE posts to {cpePath})");
                    kept.Add("Path=/");
                    changed = true;
                    continue;
                }
            }
            kept.Add(attr);
        }
        return kept.Count == parts.Count && !changed ? value : string.Join("; ", kept);
    }

    /// <summary>RFC 6265 §5.1.4 path-match.</summary>
    internal static bool PathMatches(string requestPathAndQuery, string cookiePath)
    {
        var q = requestPathAndQuery.IndexOf('?', StringComparison.Ordinal);
        var path = q >= 0 ? requestPathAndQuery[..q] : requestPathAndQuery;
        if (path.Length == 0)
        {
            path = "/";
        }
        if (string.Equals(path, cookiePath, StringComparison.Ordinal))
        {
            return true;
        }
        return path.StartsWith(cookiePath, StringComparison.Ordinal)
            && (cookiePath.EndsWith('/') || path[cookiePath.Length] == '/');
    }

    private static void AddNote(IList<string> notes, string note)
    {
        if (!notes.Contains(note))
        {
            notes.Add(note);
        }
    }
}
