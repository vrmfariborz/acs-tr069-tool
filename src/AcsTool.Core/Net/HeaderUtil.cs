namespace AcsTool.Core.Net;

public static class HeaderUtil
{
    /// <summary>RFC 9110 §7.6.1 hop-by-hop headers (plus Proxy-* and legacy Proxy-Connection).</summary>
    public static readonly IReadOnlySet<string> HopByHop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Connection", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailer", "Transfer-Encoding", "Upgrade",
    };

    /// <summary>Headers that belong on HttpContent rather than HttpRequestMessage.</summary>
    public static readonly IReadOnlySet<string> ContentHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type", "Content-Encoding", "Content-Language", "Content-Location", "Content-MD5",
        "Content-Disposition", "Content-Range", "Expires", "Last-Modified", "Allow",
    };

    public static bool IsHopByHop(string name) => HopByHop.Contains(name);

    /// <summary>Names listed in Connection header values (which are therefore hop-by-hop too).</summary>
    public static HashSet<string> ConnectionListed(IEnumerable<KeyValuePair<string, string>> headers)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in headers)
        {
            if (k.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var token in v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    set.Add(token);
                }
            }
        }
        return set;
    }

    /// <summary>Removes hop-by-hop headers and the headers named in Connection.</summary>
    public static IEnumerable<KeyValuePair<string, string>> EndToEnd(IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        var listed = ConnectionListed(headers);
        return headers.Where(h => !IsHopByHop(h.Key) && !listed.Contains(h.Key));
    }

    public static string? Get(IEnumerable<KeyValuePair<string, string>> headers, string name) =>
        headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    public static IEnumerable<string> GetAll(IEnumerable<KeyValuePair<string, string>> headers, string name) =>
        headers.Where(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value);
}
