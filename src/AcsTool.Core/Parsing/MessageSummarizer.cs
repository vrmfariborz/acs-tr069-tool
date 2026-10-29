using System.Globalization;
using AcsTool.Core.Cwmp;
using AcsTool.Core.Model;

namespace AcsTool.Core.Parsing;

public sealed record MessageSummary(CwmpMessageKind Kind, string Title, string OneLine,
    IReadOnlyList<KeyValuePair<string, string>> Fields, DeviceIdStruct? DeviceId,
    IReadOnlyList<EventStruct>? Events, CwmpFault? Fault, bool? HoldRequests, string? CwmpId,
    CwmpVersion Version, IReadOnlyList<string> Warnings)
{
    public static MessageSummary None { get; } = new(CwmpMessageKind.Empty, "Empty", "Empty", [], null, null, null, null, null, CwmpVersion.Unknown, []);
}

public interface IMessageSummarizer
{
    MessageSummary Summarize(ParseResult result);
}

/// <summary>One-line titles and stable key/value fields per message (FR-C-04).</summary>
public sealed class MessageSummarizer : IMessageSummarizer
{
    /// <summary>Per-list cap on summary fields (parameters, names, attributes, SPV faults).</summary>
    public const int MaxListedItems = 200;

    private static string N(long v) => v.ToString(CultureInfo.InvariantCulture);

    public MessageSummary Summarize(ParseResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var env = result.Envelope;
        var fields = new List<KeyValuePair<string, string>>();
        var warnings = result.Warnings.Concat(result.Errors.Select(e => "error: " + e)).ToList();
        void F(string k, string v) => fields.Add(new(k, v));

        switch (result.Kind)
        {
            case CwmpMessageKind.Empty:
                return new MessageSummary(CwmpMessageKind.Empty, "Empty", "Empty", [new("Method", "(empty)")], null, null, null, null, null, CwmpVersion.Unknown, warnings);
            case CwmpMessageKind.NotXml:
                {
                    var t = result.ErrorLine is { } line ? $"Not XML (line {N(line)})" : "Not XML";
                    F("Method", "(not XML)");
                    foreach (var e in result.Errors)
                    {
                        F("Error", e);
                    }
                    return new MessageSummary(result.Kind, t, t, fields, null, null, null, null, null, CwmpVersion.Unknown, warnings);
                }
            case CwmpMessageKind.NotSoap:
                F("Method", "(not SOAP)");
                foreach (var e in result.Errors)
                {
                    F("Error", e);
                }
                return new MessageSummary(result.Kind, "Not SOAP", "Not SOAP", fields, null, null, null, null, null, CwmpVersion.Unknown, warnings);
        }

        var header = env!.Header;
        F("Method", env.MethodLocalName);
        if (header.Id is not null)
        {
            F("cwmp:ID", header.Id);
        }
        if (header.HoldRequests is { } hr)
        {
            F("HoldRequests", hr ? "true" : "false");
        }
        if (header.SessionTimeout is { } st)
        {
            F("SessionTimeout", N(st));
        }
        F("Version", env.CwmpNamespace ?? "(none)");

        string title;
        var oneLine = (string?)null;
        DeviceIdStruct? deviceId = null;
        IReadOnlyList<EventStruct>? events = null;
        CwmpFault? fault = null;

        switch (env.Message)
        {
            case InformMessage inf:
                deviceId = inf.DeviceId;
                events = inf.Events;
                title = $"Inform {inf.DeviceId.Key} [{string.Join(", ", inf.Events.Select(e => e.EventCode))}]";
                oneLine = $"{title} {N(inf.Parameters.Count)} params";
                F("DeviceId", $"{inf.DeviceId.Manufacturer} / {inf.DeviceId.OUI} / {inf.DeviceId.ProductClass} / {inf.DeviceId.SerialNumber}");
                F("Events", string.Join("; ", inf.Events.Select(e => string.IsNullOrEmpty(e.CommandKey) ? e.EventCode : $"{e.EventCode} ({e.CommandKey})")));
                F("RetryCount", N(inf.RetryCount));
                F("MaxEnvelopes", N(inf.MaxEnvelopes));
                F("CurrentTime", inf.CurrentTime);
                AddParams(fields, inf.Parameters);
                break;
            case InformResponseMessage ir:
                title = "InformResponse";
                F("MaxEnvelopes", N(ir.MaxEnvelopes));
                break;
            case GetParameterValuesMessage gpv:
                title = $"GPV ×{N(gpv.Names.Count)}";
                AddNames(fields, gpv.Names);
                break;
            case GetParameterValuesResponseMessage gpvr:
                title = $"GPVResponse ×{N(gpvr.Values.Count)}";
                AddParams(fields, gpvr.Values);
                break;
            case SetParameterValuesMessage spv:
                title = string.IsNullOrEmpty(spv.ParameterKey) ? $"SPV ×{N(spv.Values.Count)}" : $"SPV ×{N(spv.Values.Count)} key={spv.ParameterKey}";
                F("ParameterKey", spv.ParameterKey);
                AddParams(fields, spv.Values);
                break;
            case SetParameterValuesResponseMessage spvr:
                title = $"SPVResponse status={N(spvr.Status)}";
                F("Status", N(spvr.Status));
                break;
            case GetParameterNamesMessage gpn:
                title = gpn.NextLevel ? $"GPN {gpn.ParameterPath} (next level)" : $"GPN {gpn.ParameterPath}";
                F("ParameterPath", gpn.ParameterPath);
                F("NextLevel", gpn.NextLevel ? "true" : "false");
                break;
            case GetParameterNamesResponseMessage gpnr:
                title = $"GPNResponse ×{N(gpnr.List.Count)}";
                AddCapped(fields, gpnr.List, p => new("Name", $"{p.Name} ({(p.Writable ? "W" : "R")})"));
                break;
            case GetParameterAttributesMessage gpa:
                title = $"GPA ×{N(gpa.Names.Count)}";
                AddNames(fields, gpa.Names);
                break;
            case GetParameterAttributesResponseMessage gpar:
                title = $"GPAResponse ×{N(gpar.List.Count)}";
                AddCapped(fields, gpar.List, a => new(a.Name, $"Notification={N(a.Notification)} AccessList={string.Join(",", a.AccessList)}"));
                break;
            case SetParameterAttributesMessage spa:
                title = $"SPA ×{N(spa.List.Count)}";
                AddCapped(fields, spa.List, a => new(a.Name,
                    $"Notification={(a.NotificationChange ? N(a.Notification) : "-")} AccessList={(a.AccessListChange ? string.Join(",", a.AccessList) : "-")}"));
                break;
            case AddObjectMessage ao:
                title = $"AddObject {ao.ObjectName}";
                F("ObjectName", ao.ObjectName);
                F("ParameterKey", ao.ParameterKey);
                break;
            case AddObjectResponseMessage aor:
                title = $"AddObjectResponse #{N(aor.InstanceNumber)}";
                F("InstanceNumber", N(aor.InstanceNumber));
                F("Status", N(aor.Status));
                break;
            case DeleteObjectMessage dobj:
                title = $"DeleteObject {dobj.ObjectName}";
                F("ObjectName", dobj.ObjectName);
                F("ParameterKey", dobj.ParameterKey);
                break;
            case DeleteObjectResponseMessage dor:
                title = "DeleteObjectResponse";
                F("Status", N(dor.Status));
                break;
            case RebootMessage rb:
                title = string.IsNullOrEmpty(rb.CommandKey) ? "Reboot" : $"Reboot key={rb.CommandKey}";
                F("CommandKey", rb.CommandKey);
                break;
            case DownloadMessage dl:
                title = $"Download {dl.FileType}";
                F("CommandKey", dl.CommandKey);
                F("FileType", dl.FileType);
                F("URL", dl.Url);
                F("FileSize", dl.FileSize.ToString(CultureInfo.InvariantCulture));
                F("DelaySeconds", N(dl.DelaySeconds));
                break;
            case DownloadResponseMessage dlr:
                title = $"DownloadResponse status={N(dlr.Status)}";
                F("Status", N(dlr.Status));
                F("StartTime", dlr.StartTime);
                F("CompleteTime", dlr.CompleteTime);
                break;
            case TransferCompleteMessage tc:
                title = tc.FaultCode == 0 ? $"TransferComplete key={tc.CommandKey}" : $"TransferComplete key={tc.CommandKey} fault={N(tc.FaultCode)}";
                F("CommandKey", tc.CommandKey);
                F("FaultCode", N(tc.FaultCode));
                F("FaultString", tc.FaultString);
                F("StartTime", tc.StartTime);
                F("CompleteTime", tc.CompleteTime);
                break;
            case ScheduleInformMessage si:
                title = $"ScheduleInform {N(si.DelaySeconds)}s";
                F("DelaySeconds", N(si.DelaySeconds));
                F("CommandKey", si.CommandKey);
                break;
            case GetRpcMethodsResponseMessage grm:
                title = $"GetRPCMethodsResponse ×{N(grm.Methods.Count)}";
                F("Methods", string.Join(", ", grm.Methods));
                break;
            case CwmpFault f:
                fault = f;
                title = f.FaultCode == 0 ? $"Fault {f.SoapFaultCode} {f.SoapFaultString}".TrimEnd() : $"Fault {N(f.FaultCode)} {f.FaultString}".TrimEnd();
                F("faultcode", f.SoapFaultCode);
                F("faultstring", f.SoapFaultString);
                F("FaultCode", N(f.FaultCode));
                F("FaultString", f.FaultString);
                AddCapped(fields, f.SpvFaults, i => new("SPV fault", $"{i.ParameterName}: {N(i.FaultCode)} {i.FaultString}"));
                break;
            default:
                title = result.Kind == CwmpMessageKind.Unknown
                    ? (env.MethodLocalName.Length == 0 ? "Unknown (empty Body)" : $"Unknown {env.MethodLocalName}")
                    : result.Kind.ShortName();
                break;
        }

        return new MessageSummary(result.Kind, title, oneLine ?? title, fields, deviceId, events, fault,
            header.HoldRequests, header.Id, env.Version, warnings);
    }

    private static void AddParams(List<KeyValuePair<string, string>> fields, IReadOnlyList<ParameterValue> ps)
    {
        fields.Add(new($"Params ({N(ps.Count)})", ps.Count == 0 ? "—" : ps[0].Name + (ps.Count > 1 ? " …" : string.Empty)));
        AddCapped(fields, ps, p => new(p.Name, $"{p.Value} ({p.XsdType})"));
    }

    private static void AddNames(List<KeyValuePair<string, string>> fields, IReadOnlyList<string> names) =>
        AddCapped(fields, names, n => new("Name", n));

    /// <summary>
    /// Adds at most <see cref="MaxListedItems"/> per-item fields plus a "… and K more" field, so a summary stays small even for
    /// huge GPV/GPN messages (the full content is in the captured body / Raw view; review M4).
    /// </summary>
    private static void AddCapped<T>(List<KeyValuePair<string, string>> fields, IReadOnlyList<T> items, Func<T, KeyValuePair<string, string>> field)
    {
        var take = Math.Min(items.Count, MaxListedItems);
        for (var i = 0; i < take; i++)
        {
            fields.Add(field(items[i]));
        }
        if (items.Count > take)
        {
            fields.Add(new($"… and {N(items.Count - take)} more", "see the Raw view"));
        }
    }
}
