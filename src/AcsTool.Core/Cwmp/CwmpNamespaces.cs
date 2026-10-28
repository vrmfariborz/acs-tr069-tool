namespace AcsTool.Core.Cwmp;

public static class CwmpNamespaces
{
    public const string SoapEnv = "http://schemas.xmlsoap.org/soap/envelope/";
    public const string SoapEnv12 = "http://www.w3.org/2003/05/soap-envelope";
    public const string SoapEnc = "http://schemas.xmlsoap.org/soap/encoding/";
    public const string Xsd = "http://www.w3.org/2001/XMLSchema";
    public const string Xsi = "http://www.w3.org/2001/XMLSchema-instance";
    public const string CwmpPrefix = "urn:dslforum-org:cwmp-1-";

    public static string For(CwmpVersion v) => v switch
    {
        CwmpVersion.V1_0 => CwmpPrefix + "0",
        CwmpVersion.V1_1 => CwmpPrefix + "1",
        CwmpVersion.V1_2 => CwmpPrefix + "2",
        CwmpVersion.V1_3 => CwmpPrefix + "3",
        CwmpVersion.V1_4 => CwmpPrefix + "4",
        _ => CwmpPrefix + "0",
    };

    /// <summary>Maps <c>urn:dslforum-org:cwmp-1-[0-4]</c> to a version; anything else is <see cref="CwmpVersion.Unknown"/>.</summary>
    public static CwmpVersion Detect(string? ns)
    {
        if (ns is null || ns.Length != CwmpPrefix.Length + 1 || !ns.StartsWith(CwmpPrefix, StringComparison.Ordinal))
        {
            return CwmpVersion.Unknown;
        }
        return ns[^1] switch
        {
            '0' => CwmpVersion.V1_0,
            '1' => CwmpVersion.V1_1,
            '2' => CwmpVersion.V1_2,
            '3' => CwmpVersion.V1_3,
            '4' => CwmpVersion.V1_4,
            _ => CwmpVersion.Unknown,
        };
    }

    public static string Display(CwmpVersion v) => v == CwmpVersion.Unknown ? "unknown" : "cwmp-1-" + ((int)v).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
