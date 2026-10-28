using System.Globalization;

namespace AcsTool.Core.Cwmp;

public static class XsdTypes
{
    public const string String = "xsd:string";
    public const string Int = "xsd:int";
    public const string UnsignedInt = "xsd:unsignedInt";
    public const string Long = "xsd:long";
    public const string UnsignedLong = "xsd:unsignedLong";
    public const string Boolean = "xsd:boolean";
    public const string DateTime = "xsd:dateTime";
    public const string Base64 = "xsd:base64";
    public const string HexBinary = "xsd:hexBinary";

    public static IReadOnlyList<string> All { get; } = [String, Int, UnsignedInt, Long, UnsignedLong, Boolean, DateTime, Base64, HexBinary];

    /// <summary>Normalizes "string", "xsd:string", "xs:string", "soap-enc:base64" etc. to the "xsd:" form.</summary>
    public static string Normalize(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return String;
        }
        var t = type.Trim();
        var colon = t.IndexOf(':', StringComparison.Ordinal);
        var local = colon >= 0 ? t[(colon + 1)..] : t;
        if (local.Equals("base64Binary", StringComparison.Ordinal))
        {
            local = "base64";
        }
        return "xsd:" + local;
    }

    public static bool IsKnown(string? type) => All.Contains(Normalize(type));

    /// <summary>Validates a lexical value against a (normalized) xsd type.</summary>
    public static bool IsValidValue(string type, string? value)
    {
        value ??= string.Empty;
        var inv = CultureInfo.InvariantCulture;
        switch (Normalize(type))
        {
            case String:
                return true;
            case Int:
                return int.TryParse(value, NumberStyles.AllowLeadingSign, inv, out _);
            case UnsignedInt:
                return uint.TryParse(value, NumberStyles.None, inv, out _);
            case Long:
                return long.TryParse(value, NumberStyles.AllowLeadingSign, inv, out _);
            case UnsignedLong:
                return ulong.TryParse(value, NumberStyles.None, inv, out _);
            case Boolean:
                return value is "0" or "1" or "true" or "false";
            case DateTime:
                return value.Length > 0 && DateTimeOffset.TryParse(value, inv, DateTimeStyles.AssumeUniversal, out _);
            case Base64:
                return value.Length % 4 == 0 && Convert.TryFromBase64String(value, new byte[value.Length], out _);
            case HexBinary:
                return value.Length % 2 == 0 && value.All(Uri.IsHexDigit);
            default:
                return true;
        }
    }
}
