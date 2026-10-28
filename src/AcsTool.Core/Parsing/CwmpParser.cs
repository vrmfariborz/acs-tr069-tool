using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using AcsTool.Core.Cwmp;
using AcsTool.Core.Model;

namespace AcsTool.Core.Parsing;

/// <summary>
/// Tolerant, XXE-safe CWMP SOAP 1.1 parser (plan §3.5.2). Elements are matched by local name; deviations
/// from the standard produce warnings instead of failures. Never throws to callers.
/// </summary>
public sealed class CwmpParser : ICwmpParser
{
    public const int MaxCharactersInDocument = 20_000_000;

    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MaxCharactersInDocument,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        CloseInput = true,
    };

    public ParseResult Parse(ReadOnlySpan<byte> body, string? contentType = null)
    {
        try
        {
            return ParseCore(body, contentType);
        }
#pragma warning disable CA1031 // never throw to callers (FR-C-07)
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return new ParseResult(null, CwmpMessageKind.NotXml, [], ["Parser failure: " + ex.Message]);
        }
    }

    private static ParseResult ParseCore(ReadOnlySpan<byte> body, string? contentType)
    {
        if (IsBlank(body))
        {
            return ParseResult.EmptyBody;
        }

        var warnings = new List<string>();
        XDocument doc;
        try
        {
            using var stream = new MemoryStream(body.ToArray(), writable: false);
            using var reader = XmlReader.Create(stream, ReaderSettings);
            doc = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            return new ParseResult(null, CwmpMessageKind.NotXml, [], [ex.Message]) { ErrorLine = ex.LineNumber };
        }

        CheckCharset(body, doc, contentType, warnings);

        var root = doc.Root;
        if (root is null || root.Name.LocalName != "Envelope")
        {
            return new ParseResult(null, CwmpMessageKind.NotSoap, warnings,
                [$"No SOAP Envelope (root element is '{root?.Name.LocalName}')"]);
        }

        var envNs = root.Name.NamespaceName;
        if (envNs == CwmpNamespaces.SoapEnv12)
        {
            warnings.Add("non-standard SOAP envelope namespace: SOAP 1.2 (" + envNs + ")");
        }
        else if (envNs != CwmpNamespaces.SoapEnv)
        {
            warnings.Add("non-standard SOAP envelope namespace: '" + envNs + "'");
        }

        var headerEl = Child(root, "Header");
        var bodyEl = Child(root, "Body");
        if (bodyEl is null)
        {
            return new ParseResult(null, CwmpMessageKind.NotSoap, warnings, ["SOAP Envelope has no Body"]);
        }

        var header = ParseHeader(headerEl, warnings);
        var bodyChildren = bodyEl.Elements().ToList();
        if (bodyChildren.Count > 1)
        {
            warnings.Add("multiple body elements");
        }

        var method = bodyChildren.FirstOrDefault();
        var cwmpNs = DetectCwmpNamespace(method, headerEl);
        var version = CwmpNamespaces.Detect(cwmpNs);
        if (method is null)
        {
            warnings.Add("empty SOAP Body");
            var envEmpty = new CwmpEnvelope(version, cwmpNs, header, CwmpMessageKind.Unknown, string.Empty, null, bodyEl);
            return new ParseResult(envEmpty, CwmpMessageKind.Unknown, warnings, []);
        }

        var local = method.Name.LocalName;
        var kind = KindFor(local);
        if (kind != CwmpMessageKind.Fault && CwmpNamespaces.Detect(method.Name.NamespaceName) == CwmpVersion.Unknown)
        {
            warnings.Add($"method element '{local}' is not in a cwmp-1-x namespace ('{method.Name.NamespaceName}')");
        }

        CwmpMessage? message = null;
        var errors = new List<string>();
        try
        {
            message = kind switch
            {
                CwmpMessageKind.Unknown => null,
                _ => ParseMessage(kind, method, warnings),
            };
        }
        catch (FormatException ex)
        {
            errors.Add($"{local}: {ex.Message}");
        }

        var env = new CwmpEnvelope(version, cwmpNs, header, kind, local, message, method);
        return new ParseResult(env, kind, warnings, errors);
    }

    private static bool IsBlank(ReadOnlySpan<byte> body)
    {
        var span = body;
        var utf16 = false;
        if (span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            span = span[3..];
        }
        else if (span.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) || span.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            span = span[2..];
            utf16 = true;
        }
        foreach (var b in span)
        {
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                continue;
            }
            if (utf16 && b == 0)
            {
                continue;
            }
            return false;
        }
        return true;
    }

    private static void CheckCharset(ReadOnlySpan<byte> body, XDocument doc, string? contentType, List<string> warnings)
    {
        var declared = CharsetOf(contentType);
        if (declared is null)
        {
            return;
        }
        string detected;
        if (body.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) || body.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            detected = "utf-16";
        }
        else
        {
            detected = doc.Declaration?.Encoding ?? "utf-8";
        }
        if (!SameCharset(declared, detected))
        {
            warnings.Add($"Content-Type charset '{declared}' disagrees with document encoding '{detected}'");
        }
    }

    internal static string? CharsetOf(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
        {
            return null;
        }
        foreach (var part in contentType.Split(';'))
        {
            var p = part.Trim();
            if (p.StartsWith("charset=", StringComparison.OrdinalIgnoreCase))
            {
                return p["charset=".Length..].Trim().Trim('"', '\'');
            }
        }
        return null;
    }

    private static bool SameCharset(string a, string b)
    {
        static string N(string s) => s.Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        var na = N(a);
        var nb = N(b);
        if (na == nb)
        {
            return true;
        }
        // utf-16 vs utf-16le/be are compatible for our purposes
        return na.StartsWith("UTF16", StringComparison.Ordinal) && nb.StartsWith("UTF16", StringComparison.Ordinal);
    }

    private static string? DetectCwmpNamespace(XElement? method, XElement? header)
    {
        if (method is not null)
        {
            var ns = method.Name.NamespaceName;
            if (CwmpNamespaces.Detect(ns) != CwmpVersion.Unknown)
            {
                return ns;
            }
            if (method.Name.LocalName == "Fault")
            {
                var inner = method.Descendants().FirstOrDefault(d => d.Name.LocalName == "Fault");
                if (inner is not null && CwmpNamespaces.Detect(inner.Name.NamespaceName) != CwmpVersion.Unknown)
                {
                    return inner.Name.NamespaceName;
                }
            }
        }
        var id = header is null ? null : Child(header, "ID");
        if (id is not null && id.Name.NamespaceName.Length > 0)
        {
            return id.Name.NamespaceName;
        }
        var fallback = method?.Name.NamespaceName;
        return string.IsNullOrEmpty(fallback) ? null : fallback;
    }

    private static CwmpHeader ParseHeader(XElement? header, List<string> warnings)
    {
        if (header is null)
        {
            return CwmpHeader.None;
        }
        string? id = null;
        var must = true;
        bool? hold = null;
        uint? timeout = null;
        bool? noMore = null;
        foreach (var el in header.Elements())
        {
            switch (el.Name.LocalName)
            {
                case "ID":
                    id = el.Value;
                    var mu = el.Attributes().FirstOrDefault(a => a.Name.LocalName == "mustUnderstand");
                    must = mu is not null && mu.Value.Trim() is "1" or "true";
                    break;
                case "HoldRequests":
                    hold = ParseBool(el.Value);
                    break;
                case "SessionTimeout":
                    if (uint.TryParse(el.Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var t))
                    {
                        timeout = t;
                    }
                    else
                    {
                        warnings.Add("invalid SessionTimeout header");
                    }
                    break;
                case "NoMoreRequests":
                    noMore = ParseBool(el.Value);
                    break;
            }
        }
        return new CwmpHeader(id, must, hold, timeout, noMore);
    }

    private static CwmpMessageKind KindFor(string local)
    {
        if (local == "Fault")
        {
            return CwmpMessageKind.Fault;
        }
        if (Enum.TryParse<CwmpMessageKind>(local, ignoreCase: false, out var k)
            && k is not (CwmpMessageKind.Empty or CwmpMessageKind.Unknown or CwmpMessageKind.NotXml or CwmpMessageKind.NotSoap)
            && k.ToString() == local)
        {
            return k;
        }
        return CwmpMessageKind.Unknown;
    }

    private static CwmpMessage? ParseMessage(CwmpMessageKind kind, XElement m, List<string> w)
    {
        switch (kind)
        {
            case CwmpMessageKind.Inform:
                return ParseInform(m, w);
            case CwmpMessageKind.InformResponse:
                return new InformResponseMessage(U(m, "MaxEnvelopes", w));
            case CwmpMessageKind.GetRPCMethods:
                return new GetRpcMethodsMessage();
            case CwmpMessageKind.GetRPCMethodsResponse:
                return new GetRpcMethodsResponseMessage(Items(Child(m, "MethodList"), "MethodList", w).Select(e => e.Value.Trim()).ToList());
            case CwmpMessageKind.GetParameterNames:
                return new GetParameterNamesMessage(S(m, "ParameterPath").Trim(), Bool(m, "NextLevel", w));
            case CwmpMessageKind.GetParameterNamesResponse:
                return new GetParameterNamesResponseMessage(Items(Child(m, "ParameterList"), "ParameterList", w)
                    .Select(e => new ParameterInfo(S(e, "Name").Trim(), Bool(e, "Writable", w))).ToList());
            case CwmpMessageKind.GetParameterValues:
                return new GetParameterValuesMessage(Names(m, w));
            case CwmpMessageKind.GetParameterValuesResponse:
                return new GetParameterValuesResponseMessage(ParamValues(Child(m, "ParameterList"), w, warnMissingType: false));
            case CwmpMessageKind.SetParameterValues:
                return new SetParameterValuesMessage(ParamValues(Child(m, "ParameterList"), w, warnMissingType: false), S(m, "ParameterKey"));
            case CwmpMessageKind.SetParameterValuesResponse:
                return new SetParameterValuesResponseMessage(I(m, "Status", w));
            case CwmpMessageKind.GetParameterAttributes:
                return new GetParameterAttributesMessage(Names(m, w));
            case CwmpMessageKind.GetParameterAttributesResponse:
                return new GetParameterAttributesResponseMessage(Items(Child(m, "ParameterList"), "ParameterList", w)
                    .Select(e => new ParameterAttribute(S(e, "Name").Trim(), I(e, "Notification", w),
                        Items(Child(e, "AccessList"), "AccessList", w).Select(x => x.Value.Trim()).ToList())).ToList());
            case CwmpMessageKind.SetParameterAttributes:
                return new SetParameterAttributesMessage(Items(Child(m, "ParameterList"), "ParameterList", w)
                    .Select(e => new SetParameterAttributeStruct(S(e, "Name").Trim(), Bool(e, "NotificationChange", w),
                        I(e, "Notification", w), Bool(e, "AccessListChange", w),
                        Items(Child(e, "AccessList"), "AccessList", w).Select(x => x.Value.Trim()).ToList())).ToList());
            case CwmpMessageKind.AddObject:
                return new AddObjectMessage(S(m, "ObjectName").Trim(), S(m, "ParameterKey"));
            case CwmpMessageKind.AddObjectResponse:
                return new AddObjectResponseMessage(U(m, "InstanceNumber", w), I(m, "Status", w));
            case CwmpMessageKind.DeleteObject:
                return new DeleteObjectMessage(S(m, "ObjectName").Trim(), S(m, "ParameterKey"));
            case CwmpMessageKind.DeleteObjectResponse:
                return new DeleteObjectResponseMessage(I(m, "Status", w));
            case CwmpMessageKind.Reboot:
                return new RebootMessage(S(m, "CommandKey"));
            case CwmpMessageKind.FactoryReset:
                return new FactoryResetMessage();
            case CwmpMessageKind.Download:
                return new DownloadMessage(S(m, "CommandKey"), S(m, "FileType"), S(m, "URL").Trim(), S(m, "Username"),
                    S(m, "Password"), UL(m, "FileSize", w), S(m, "TargetFileName"), U(m, "DelaySeconds", w),
                    S(m, "SuccessURL"), S(m, "FailureURL"));
            case CwmpMessageKind.DownloadResponse:
                return new DownloadResponseMessage(I(m, "Status", w), S(m, "StartTime").Trim(), S(m, "CompleteTime").Trim());
            case CwmpMessageKind.TransferComplete:
                {
                    var fs = Child(m, "FaultStruct");
                    return new TransferCompleteMessage(S(m, "CommandKey"), fs is null ? 0 : U(fs, "FaultCode", w),
                        fs is null ? string.Empty : S(fs, "FaultString"), S(m, "StartTime").Trim(), S(m, "CompleteTime").Trim());
                }
            case CwmpMessageKind.ScheduleInform:
                return new ScheduleInformMessage(U(m, "DelaySeconds", w), S(m, "CommandKey"));
            case CwmpMessageKind.TransferCompleteResponse:
            case CwmpMessageKind.RebootResponse:
            case CwmpMessageKind.FactoryResetResponse:
            case CwmpMessageKind.ScheduleInformResponse:
            case CwmpMessageKind.SetParameterAttributesResponse:
                return new EmptyResponseMessage(kind);
            case CwmpMessageKind.Fault:
                return ParseFault(m, w);
            default:
                return null;
        }
    }

    private static InformMessage ParseInform(XElement m, List<string> w)
    {
        var dev = Child(m, "DeviceId");
        if (dev is null)
        {
            w.Add("Inform has no DeviceId");
        }
        var deviceId = new DeviceIdStruct(
            dev is null ? string.Empty : S(dev, "Manufacturer"),
            dev is null ? string.Empty : S(dev, "OUI").Trim(),
            dev is null ? string.Empty : S(dev, "ProductClass"),
            dev is null ? string.Empty : S(dev, "SerialNumber").Trim());

        var events = Items(Child(m, "Event"), "Event", w)
            .Select(e => new EventStruct(S(e, "EventCode").Trim(), S(e, "CommandKey")))
            .ToList();

        var currentTime = S(m, "CurrentTime");
        DateTimeOffset? parsed = null;
        if (DateTimeOffset.TryParse(currentTime.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
        {
            parsed = dto;
        }
        else
        {
            w.Add($"CurrentTime '{currentTime}' is not a valid ISO-8601 date-time");
        }

        return new InformMessage(deviceId, events, U(m, "MaxEnvelopes", w), currentTime, U(m, "RetryCount", w),
            ParamValues(Child(m, "ParameterList"), w, warnMissingType: true))
        {
            CurrentTimeParsed = parsed,
        };
    }

    private static CwmpFault ParseFault(XElement m, List<string> w)
    {
        var soapCode = S(m, "faultcode").Trim();
        var soapString = S(m, "faultstring");
        var detail = Child(m, "detail") ?? Child(m, "Detail");
        var cf = detail is null ? null : Child(detail, "Fault");
        if (cf is null)
        {
            w.Add("SOAP Fault without cwmp:Fault detail");
            return new CwmpFault(soapCode, soapString, 0, string.Empty, []);
        }
        var items = cf.Elements().Where(e => e.Name.LocalName == "SetParameterValuesFault")
            .Select(e => new SetParameterValuesFaultItem(S(e, "ParameterName").Trim(), U(e, "FaultCode", w), S(e, "FaultString")))
            .ToList();
        return new CwmpFault(soapCode, soapString, U(cf, "FaultCode", w), S(cf, "FaultString"), items);
    }

    private static List<string> Names(XElement m, List<string> w) =>
        Items(Child(m, "ParameterNames"), "ParameterNames", w).Select(e => e.Value.Trim()).ToList();

    private static List<ParameterValue> ParamValues(XElement? list, List<string> w, bool warnMissingType)
    {
        var result = new List<ParameterValue>();
        var missingType = 0;
        foreach (var e in Items(list, "ParameterList", w))
        {
            var name = S(e, "Name").Trim();
            var valueEl = Child(e, "Value");
            var type = valueEl?.Attributes().FirstOrDefault(a => a.Name.LocalName == "type")?.Value;
            if (string.IsNullOrEmpty(type))
            {
                missingType++;
                type = XsdTypes.String;
            }
            result.Add(new ParameterValue(name, valueEl?.Value ?? string.Empty, type));
        }
        if (warnMissingType && missingType > 0)
        {
            w.Add($"{missingType} parameter value(s) without xsi:type (assumed xsd:string)");
        }
        return result;
    }

    private static List<XElement> Items(XElement? array, string desc, List<string> w)
    {
        if (array is null)
        {
            return [];
        }
        var items = array.Elements().ToList();
        var at = array.Attributes().FirstOrDefault(a => a.Name.LocalName == "arrayType")?.Value;
        if (at is not null)
        {
            var open = at.LastIndexOf('[');
            var close = at.LastIndexOf(']');
            if (open >= 0 && close > open
                && int.TryParse(at.AsSpan(open + 1, close - open - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var declared)
                && declared != items.Count)
            {
                w.Add($"{desc}: arrayType declares {declared} items but {items.Count} found");
            }
        }
        return items;
    }

    internal static XElement? Child(XElement e, string local)
    {
        foreach (var c in e.Elements())
        {
            if (c.Name.LocalName == local)
            {
                return c;
            }
        }
        return null;
    }

    private static string S(XElement e, string local) => Child(e, local)?.Value ?? string.Empty;

    private static uint U(XElement e, string local, List<string> w)
    {
        var el = Child(e, local);
        if (el is null)
        {
            return 0;
        }
        if (uint.TryParse(el.Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var v))
        {
            return v;
        }
        w.Add($"{local}: '{el.Value}' is not an unsignedInt");
        return 0;
    }

    private static ulong UL(XElement e, string local, List<string> w)
    {
        var el = Child(e, local);
        if (el is null)
        {
            return 0;
        }
        if (ulong.TryParse(el.Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var v))
        {
            return v;
        }
        w.Add($"{local}: '{el.Value}' is not an unsignedLong");
        return 0;
    }

    private static int I(XElement e, string local, List<string> w)
    {
        var el = Child(e, local);
        if (el is null)
        {
            return 0;
        }
        if (int.TryParse(el.Value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v))
        {
            return v;
        }
        w.Add($"{local}: '{el.Value}' is not an int");
        return 0;
    }

    private static bool Bool(XElement e, string local, List<string> w)
    {
        var el = Child(e, local);
        if (el is null)
        {
            return false;
        }
        var b = ParseBool(el.Value);
        if (b is null)
        {
            w.Add($"{local}: '{el.Value}' is not a boolean");
        }
        return b ?? false;
    }

    private static bool? ParseBool(string s) => s.Trim() switch
    {
        "1" or "true" or "True" or "TRUE" => true,
        "0" or "false" or "False" or "FALSE" => false,
        _ => null,
    };

    /// <summary>Decodes body bytes to text for display (BOM-aware, UTF-8 fallback).</summary>
    public static string DecodeText(ReadOnlySpan<byte> body)
    {
        if (body.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return Encoding.Unicode.GetString(body[2..]);
        }
        if (body.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return Encoding.BigEndianUnicode.GetString(body[2..]);
        }
        if (body.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return Encoding.UTF8.GetString(body[3..]);
        }
        return Encoding.UTF8.GetString(body);
    }
}
