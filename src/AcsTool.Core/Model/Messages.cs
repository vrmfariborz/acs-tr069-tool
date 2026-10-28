using System.Text.Json.Serialization;

namespace AcsTool.Core.Model;

public abstract record CwmpMessage;

public sealed record DeviceIdStruct(string Manufacturer, string OUI, string ProductClass, string SerialNumber)
{
    [JsonIgnore]
    public string Key => $"{OUI}-{SerialNumber}";
}

public sealed record EventStruct(string EventCode, string CommandKey = "");

public sealed record ParameterValue(string Name, string Value, string XsdType = "xsd:string");

public sealed record InformMessage(DeviceIdStruct DeviceId, IReadOnlyList<EventStruct> Events, uint MaxEnvelopes,
    string CurrentTime, uint RetryCount, IReadOnlyList<ParameterValue> Parameters) : CwmpMessage
{
    public DateTimeOffset? CurrentTimeParsed { get; init; }
}

public sealed record InformResponseMessage(uint MaxEnvelopes) : CwmpMessage;

public sealed record GetRpcMethodsMessage : CwmpMessage;

public sealed record GetRpcMethodsResponseMessage(IReadOnlyList<string> Methods) : CwmpMessage;

public sealed record GetParameterNamesMessage(string ParameterPath, bool NextLevel) : CwmpMessage;

public sealed record ParameterInfo(string Name, bool Writable);

public sealed record GetParameterNamesResponseMessage(IReadOnlyList<ParameterInfo> List) : CwmpMessage;

public sealed record GetParameterValuesMessage(IReadOnlyList<string> Names) : CwmpMessage;

public sealed record GetParameterValuesResponseMessage(IReadOnlyList<ParameterValue> Values) : CwmpMessage;

public sealed record SetParameterValuesMessage(IReadOnlyList<ParameterValue> Values, string ParameterKey) : CwmpMessage;

public sealed record SetParameterValuesResponseMessage(int Status) : CwmpMessage;

public sealed record ParameterAttribute(string Name, int Notification, IReadOnlyList<string> AccessList);

public sealed record GetParameterAttributesMessage(IReadOnlyList<string> Names) : CwmpMessage;

public sealed record GetParameterAttributesResponseMessage(IReadOnlyList<ParameterAttribute> List) : CwmpMessage;

public sealed record SetParameterAttributeStruct(string Name, bool NotificationChange, int Notification,
    bool AccessListChange, IReadOnlyList<string> AccessList);

public sealed record SetParameterAttributesMessage(IReadOnlyList<SetParameterAttributeStruct> List) : CwmpMessage;

public sealed record AddObjectMessage(string ObjectName, string ParameterKey) : CwmpMessage;

public sealed record AddObjectResponseMessage(uint InstanceNumber, int Status) : CwmpMessage;

public sealed record DeleteObjectMessage(string ObjectName, string ParameterKey) : CwmpMessage;

public sealed record DeleteObjectResponseMessage(int Status) : CwmpMessage;

public sealed record RebootMessage(string CommandKey) : CwmpMessage;

public sealed record FactoryResetMessage : CwmpMessage;

public sealed record DownloadMessage(string CommandKey, string FileType, string Url, string Username, string Password,
    ulong FileSize, string TargetFileName, uint DelaySeconds, string SuccessUrl, string FailureUrl) : CwmpMessage;

public sealed record DownloadResponseMessage(int Status, string StartTime, string CompleteTime) : CwmpMessage;

public sealed record TransferCompleteMessage(string CommandKey, uint FaultCode, string FaultString,
    string StartTime, string CompleteTime) : CwmpMessage;

public sealed record ScheduleInformMessage(uint DelaySeconds, string CommandKey) : CwmpMessage;

/// <summary>Body-less responses: RebootResponse, FactoryResetResponse, ScheduleInformResponse, SetParameterAttributesResponse, TransferCompleteResponse.</summary>
public sealed record EmptyResponseMessage(CwmpMessageKind Kind) : CwmpMessage;

public sealed record SetParameterValuesFaultItem(string ParameterName, uint FaultCode, string FaultString);

public sealed record CwmpFault(string SoapFaultCode, string SoapFaultString, uint FaultCode, string FaultString,
    IReadOnlyList<SetParameterValuesFaultItem> SpvFaults) : CwmpMessage;

public static class CwmpMessageExtensions
{
    public static CwmpMessageKind KindOf(this CwmpMessage message) => message switch
    {
        InformMessage => CwmpMessageKind.Inform,
        InformResponseMessage => CwmpMessageKind.InformResponse,
        GetRpcMethodsMessage => CwmpMessageKind.GetRPCMethods,
        GetRpcMethodsResponseMessage => CwmpMessageKind.GetRPCMethodsResponse,
        GetParameterNamesMessage => CwmpMessageKind.GetParameterNames,
        GetParameterNamesResponseMessage => CwmpMessageKind.GetParameterNamesResponse,
        GetParameterValuesMessage => CwmpMessageKind.GetParameterValues,
        GetParameterValuesResponseMessage => CwmpMessageKind.GetParameterValuesResponse,
        SetParameterValuesMessage => CwmpMessageKind.SetParameterValues,
        SetParameterValuesResponseMessage => CwmpMessageKind.SetParameterValuesResponse,
        GetParameterAttributesMessage => CwmpMessageKind.GetParameterAttributes,
        GetParameterAttributesResponseMessage => CwmpMessageKind.GetParameterAttributesResponse,
        SetParameterAttributesMessage => CwmpMessageKind.SetParameterAttributes,
        AddObjectMessage => CwmpMessageKind.AddObject,
        AddObjectResponseMessage => CwmpMessageKind.AddObjectResponse,
        DeleteObjectMessage => CwmpMessageKind.DeleteObject,
        DeleteObjectResponseMessage => CwmpMessageKind.DeleteObjectResponse,
        RebootMessage => CwmpMessageKind.Reboot,
        FactoryResetMessage => CwmpMessageKind.FactoryReset,
        DownloadMessage => CwmpMessageKind.Download,
        DownloadResponseMessage => CwmpMessageKind.DownloadResponse,
        TransferCompleteMessage => CwmpMessageKind.TransferComplete,
        ScheduleInformMessage => CwmpMessageKind.ScheduleInform,
        EmptyResponseMessage e => e.Kind,
        CwmpFault => CwmpMessageKind.Fault,
        _ => CwmpMessageKind.Unknown,
    };
}
