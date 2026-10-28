using System.Globalization;
using System.Text;
using System.Xml;
using AcsTool.Core.Cwmp;
using AcsTool.Core.Model;

namespace AcsTool.Core.Building;

public interface ICwmpMessageBuilder
{
    /// <summary>UTF-8 without BOM.</summary>
    byte[] Build(CwmpMessage message, CwmpVersion version, CwmpHeader header);

    string BuildString(CwmpMessage message, CwmpVersion version, CwmpHeader header);

    /// <summary>faultcode Client for argument errors (9001, 9003, 9005-9009, 9012-9019), Server otherwise.</summary>
    byte[] BuildFault(uint faultCode, string faultString, CwmpVersion version, string? cwmpId,
        IReadOnlyList<SetParameterValuesFaultItem>? spvFaults = null);
}

/// <summary>Deterministic CWMP SOAP writer (plan §3.5.3).</summary>
public sealed class CwmpMessageBuilder : ICwmpMessageBuilder
{
    private const string Env = CwmpNamespaces.SoapEnv;
    private const string Enc = CwmpNamespaces.SoapEnc;
    private const string Xsi = CwmpNamespaces.Xsi;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly XmlWriterSettings Settings = new()
    {
        OmitXmlDeclaration = true,
        Indent = true,
        IndentChars = "  ",
        NewLineChars = "\n",
        NewLineHandling = NewLineHandling.Replace,
        ConformanceLevel = ConformanceLevel.Document,
    };

    public byte[] Build(CwmpMessage message, CwmpVersion version, CwmpHeader header) =>
        Utf8NoBom.GetBytes(BuildString(message, version, header));

    public byte[] BuildFault(uint faultCode, string faultString, CwmpVersion version, string? cwmpId,
        IReadOnlyList<SetParameterValuesFaultItem>? spvFaults = null) =>
        Build(CreateFault(faultCode, faultString, spvFaults), version, new CwmpHeader(cwmpId));

    public static CwmpFault CreateFault(uint faultCode, string? faultString = null, IReadOnlyList<SetParameterValuesFaultItem>? spvFaults = null) =>
        new(FaultCodes.SoapFaultCodeFor(faultCode), "CWMP fault", faultCode, faultString ?? FaultCodes.DefaultString(faultCode), spvFaults ?? []);

    public string BuildString(CwmpMessage message, CwmpVersion version, CwmpHeader header)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(header);
        var cwmp = CwmpNamespaces.For(version);
        var sb = new StringBuilder(1024);
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        using (var w = XmlWriter.Create(sb, Settings))
        {
            w.WriteStartElement("soap-env", "Envelope", Env);
            w.WriteAttributeString("xmlns", "soap-env", null, Env);
            w.WriteAttributeString("xmlns", "soap-enc", null, Enc);
            w.WriteAttributeString("xmlns", "xsd", null, CwmpNamespaces.Xsd);
            w.WriteAttributeString("xmlns", "xsi", null, Xsi);
            w.WriteAttributeString("xmlns", "cwmp", null, cwmp);

            w.WriteStartElement("soap-env", "Header", Env);
            if (header.Id is not null)
            {
                w.WriteStartElement("cwmp", "ID", cwmp);
                if (header.IdMustUnderstand)
                {
                    w.WriteAttributeString("soap-env", "mustUnderstand", Env, "1");
                }
                w.WriteString(header.Id);
                w.WriteEndElement();
            }
            if (header.HoldRequests is { } hold)
            {
                w.WriteStartElement("cwmp", "HoldRequests", cwmp);
                w.WriteAttributeString("soap-env", "mustUnderstand", Env, "1");
                w.WriteString(hold ? "1" : "0");
                w.WriteEndElement();
            }
            if (header.SessionTimeout is { } st)
            {
                w.WriteElementString("cwmp", "SessionTimeout", cwmp, N(st));
            }
            if (header.NoMoreRequests is { } nmr)
            {
                w.WriteElementString("cwmp", "NoMoreRequests", cwmp, nmr ? "1" : "0");
            }
            w.WriteEndElement(); // Header

            w.WriteStartElement("soap-env", "Body", Env);
            WriteBody(w, message, cwmp);
            w.WriteEndElement(); // Body
            w.WriteEndElement(); // Envelope
        }
        sb.Append('\n');
        return sb.ToString();
    }

    private static string N(long v) => v.ToString(CultureInfo.InvariantCulture);

    private static string N(ulong v) => v.ToString(CultureInfo.InvariantCulture);

    private static string B(bool v) => v ? "1" : "0";

    private static void WriteBody(XmlWriter w, CwmpMessage message, string cwmp)
    {
        switch (message)
        {
            case CwmpFault f:
                w.WriteStartElement("soap-env", "Fault", Env);
                w.WriteElementString("faultcode", f.SoapFaultCode);
                w.WriteElementString("faultstring", f.SoapFaultString);
                w.WriteStartElement("detail");
                w.WriteStartElement("cwmp", "Fault", cwmp);
                w.WriteElementString("FaultCode", N(f.FaultCode));
                w.WriteElementString("FaultString", f.FaultString);
                foreach (var i in f.SpvFaults)
                {
                    w.WriteStartElement("SetParameterValuesFault");
                    w.WriteElementString("ParameterName", i.ParameterName);
                    w.WriteElementString("FaultCode", N(i.FaultCode));
                    w.WriteElementString("FaultString", i.FaultString);
                    w.WriteEndElement();
                }
                w.WriteEndElement();
                w.WriteEndElement();
                w.WriteEndElement();
                return;
            case EmptyResponseMessage e:
                w.WriteStartElement("cwmp", e.Kind.ToString(), cwmp);
                w.WriteEndElement();
                return;
        }

        var kind = message.KindOf();
        if (kind == CwmpMessageKind.Unknown)
        {
            throw new ArgumentException($"Cannot build message of type {message.GetType().Name}", nameof(message));
        }
        w.WriteStartElement("cwmp", kind.ToString(), cwmp);
        switch (message)
        {
            case InformMessage inf:
                w.WriteStartElement("DeviceId");
                w.WriteElementString("Manufacturer", inf.DeviceId.Manufacturer);
                w.WriteElementString("OUI", inf.DeviceId.OUI);
                w.WriteElementString("ProductClass", inf.DeviceId.ProductClass);
                w.WriteElementString("SerialNumber", inf.DeviceId.SerialNumber);
                w.WriteEndElement();
                StartArray(w, "Event", "cwmp:EventStruct", inf.Events.Count);
                foreach (var ev in inf.Events)
                {
                    w.WriteStartElement("EventStruct");
                    w.WriteElementString("EventCode", ev.EventCode);
                    w.WriteElementString("CommandKey", ev.CommandKey);
                    w.WriteEndElement();
                }
                w.WriteEndElement();
                w.WriteElementString("MaxEnvelopes", N(inf.MaxEnvelopes));
                w.WriteElementString("CurrentTime", inf.CurrentTime);
                w.WriteElementString("RetryCount", N(inf.RetryCount));
                WriteParamValues(w, inf.Parameters);
                break;
            case InformResponseMessage ir:
                w.WriteElementString("MaxEnvelopes", N(ir.MaxEnvelopes));
                break;
            case GetRpcMethodsMessage:
            case FactoryResetMessage:
                break;
            case GetRpcMethodsResponseMessage grm:
                WriteStrings(w, "MethodList", grm.Methods);
                break;
            case GetParameterNamesMessage gpn:
                w.WriteElementString("ParameterPath", gpn.ParameterPath);
                w.WriteElementString("NextLevel", B(gpn.NextLevel));
                break;
            case GetParameterNamesResponseMessage gpnr:
                StartArray(w, "ParameterList", "cwmp:ParameterInfoStruct", gpnr.List.Count);
                foreach (var p in gpnr.List)
                {
                    w.WriteStartElement("ParameterInfoStruct");
                    w.WriteElementString("Name", p.Name);
                    w.WriteElementString("Writable", B(p.Writable));
                    w.WriteEndElement();
                }
                w.WriteEndElement();
                break;
            case GetParameterValuesMessage gpv:
                WriteStrings(w, "ParameterNames", gpv.Names);
                break;
            case GetParameterValuesResponseMessage gpvr:
                WriteParamValues(w, gpvr.Values);
                break;
            case SetParameterValuesMessage spv:
                WriteParamValues(w, spv.Values);
                w.WriteElementString("ParameterKey", spv.ParameterKey);
                break;
            case SetParameterValuesResponseMessage spvr:
                w.WriteElementString("Status", N(spvr.Status));
                break;
            case GetParameterAttributesMessage gpa:
                WriteStrings(w, "ParameterNames", gpa.Names);
                break;
            case GetParameterAttributesResponseMessage gpar:
                StartArray(w, "ParameterList", "cwmp:ParameterAttributeStruct", gpar.List.Count);
                foreach (var a in gpar.List)
                {
                    w.WriteStartElement("ParameterAttributeStruct");
                    w.WriteElementString("Name", a.Name);
                    w.WriteElementString("Notification", N(a.Notification));
                    WriteStrings(w, "AccessList", a.AccessList);
                    w.WriteEndElement();
                }
                w.WriteEndElement();
                break;
            case SetParameterAttributesMessage spa:
                StartArray(w, "ParameterList", "cwmp:SetParameterAttributesStruct", spa.List.Count);
                foreach (var a in spa.List)
                {
                    w.WriteStartElement("SetParameterAttributesStruct");
                    w.WriteElementString("Name", a.Name);
                    w.WriteElementString("NotificationChange", B(a.NotificationChange));
                    w.WriteElementString("Notification", N(a.Notification));
                    w.WriteElementString("AccessListChange", B(a.AccessListChange));
                    WriteStrings(w, "AccessList", a.AccessList);
                    w.WriteEndElement();
                }
                w.WriteEndElement();
                break;
            case AddObjectMessage ao:
                w.WriteElementString("ObjectName", ao.ObjectName);
                w.WriteElementString("ParameterKey", ao.ParameterKey);
                break;
            case AddObjectResponseMessage aor:
                w.WriteElementString("InstanceNumber", N(aor.InstanceNumber));
                w.WriteElementString("Status", N(aor.Status));
                break;
            case DeleteObjectMessage dobj:
                w.WriteElementString("ObjectName", dobj.ObjectName);
                w.WriteElementString("ParameterKey", dobj.ParameterKey);
                break;
            case DeleteObjectResponseMessage dor:
                w.WriteElementString("Status", N(dor.Status));
                break;
            case RebootMessage rb:
                w.WriteElementString("CommandKey", rb.CommandKey);
                break;
            case DownloadMessage dl:
                w.WriteElementString("CommandKey", dl.CommandKey);
                w.WriteElementString("FileType", dl.FileType);
                w.WriteElementString("URL", dl.Url);
                w.WriteElementString("Username", dl.Username);
                w.WriteElementString("Password", dl.Password);
                w.WriteElementString("FileSize", N(dl.FileSize));
                w.WriteElementString("TargetFileName", dl.TargetFileName);
                w.WriteElementString("DelaySeconds", N(dl.DelaySeconds));
                w.WriteElementString("SuccessURL", dl.SuccessUrl);
                w.WriteElementString("FailureURL", dl.FailureUrl);
                break;
            case DownloadResponseMessage dlr:
                w.WriteElementString("Status", N(dlr.Status));
                w.WriteElementString("StartTime", dlr.StartTime);
                w.WriteElementString("CompleteTime", dlr.CompleteTime);
                break;
            case TransferCompleteMessage tc:
                w.WriteElementString("CommandKey", tc.CommandKey);
                w.WriteStartElement("FaultStruct");
                w.WriteElementString("FaultCode", N(tc.FaultCode));
                w.WriteElementString("FaultString", tc.FaultString);
                w.WriteEndElement();
                w.WriteElementString("StartTime", tc.StartTime);
                w.WriteElementString("CompleteTime", tc.CompleteTime);
                break;
            case ScheduleInformMessage si:
                w.WriteElementString("DelaySeconds", N(si.DelaySeconds));
                w.WriteElementString("CommandKey", si.CommandKey);
                break;
        }
        w.WriteEndElement();
    }

    private static void StartArray(XmlWriter w, string name, string itemType, int count)
    {
        w.WriteStartElement(name);
        w.WriteAttributeString("soap-enc", "arrayType", Enc, $"{itemType}[{N(count)}]");
    }

    private static void WriteStrings(XmlWriter w, string name, IReadOnlyList<string> values)
    {
        StartArray(w, name, "xsd:string", values.Count);
        foreach (var v in values)
        {
            w.WriteElementString("string", v);
        }
        w.WriteEndElement();
    }

    private static void WriteParamValues(XmlWriter w, IReadOnlyList<ParameterValue> ps)
    {
        StartArray(w, "ParameterList", "cwmp:ParameterValueStruct", ps.Count);
        foreach (var p in ps)
        {
            w.WriteStartElement("ParameterValueStruct");
            w.WriteElementString("Name", p.Name);
            w.WriteStartElement("Value");
            w.WriteAttributeString("xsi", "type", Xsi, p.XsdType);
            w.WriteString(p.Value);
            w.WriteEndElement();
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }
}
