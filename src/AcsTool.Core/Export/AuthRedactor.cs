using System.Text;

namespace AcsTool.Core.Export;

/// <summary>Masks Authorization values: "Basic &lt;redacted user=alice&gt;", "Digest username=\"alice\" &lt;redacted&gt;".</summary>
public static class AuthRedactor
{
    public static bool IsSensitive(string headerName) =>
        headerName.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
        || headerName.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase);

    public static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }
        var space = value.IndexOf(' ', StringComparison.Ordinal);
        var scheme = space < 0 ? value : value[..space];
        var rest = space < 0 ? string.Empty : value[(space + 1)..].Trim();
        if (scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase))
        {
            var user = BasicUser(rest);
            return user is null ? "Basic <redacted>" : $"Basic <redacted user={user}>";
        }
        if (scheme.Equals("Digest", StringComparison.OrdinalIgnoreCase))
        {
            var user = DigestParam(rest, "username");
            return user is null ? "Digest <redacted>" : $"Digest username=\"{user}\" <redacted>";
        }
        return scheme + " <redacted>";
    }

    public static string? BasicUser(string token)
    {
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(token));
            var colon = decoded.IndexOf(':', StringComparison.Ordinal);
            return colon < 0 ? decoded : decoded[..colon];
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static string? DigestParam(string parameters, string name)
    {
        foreach (var part in SplitParams(parameters))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }
            if (part[..eq].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return part[(eq + 1)..].Trim().Trim('"');
            }
        }
        return null;
    }

    /// <summary>Splits "a=1, b=\"x,y\"" on commas outside quotes.</summary>
    public static IEnumerable<string> SplitParams(string s)
    {
        var sb = new StringBuilder();
        var quoted = false;
        foreach (var c in s)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            if (c == ',' && !quoted)
            {
                yield return sb.ToString().Trim();
                sb.Clear();
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0)
        {
            yield return sb.ToString().Trim();
        }
    }
}
