namespace AcsTool.Core.Model;

public enum CwmpMessageKind
{
    Empty, Inform, InformResponse, TransferComplete, TransferCompleteResponse,
    GetRPCMethods, GetRPCMethodsResponse, GetParameterNames, GetParameterNamesResponse,
    GetParameterValues, GetParameterValuesResponse, SetParameterValues, SetParameterValuesResponse,
    GetParameterAttributes, GetParameterAttributesResponse, SetParameterAttributes, SetParameterAttributesResponse,
    AddObject, AddObjectResponse, DeleteObject, DeleteObjectResponse, Reboot, RebootResponse,
    FactoryReset, FactoryResetResponse, Download, DownloadResponse, ScheduleInform, ScheduleInformResponse,
    Fault, Unknown, NotXml, NotSoap,
}

public static class CwmpMessageKindExtensions
{
    /// <summary>True for RPCs the ACS sends to the CPE (requests the CPE must answer).</summary>
    public static bool IsAcsRequest(this CwmpMessageKind k) => k is CwmpMessageKind.GetRPCMethods
        or CwmpMessageKind.GetParameterNames or CwmpMessageKind.GetParameterValues or CwmpMessageKind.SetParameterValues
        or CwmpMessageKind.GetParameterAttributes or CwmpMessageKind.SetParameterAttributes or CwmpMessageKind.AddObject
        or CwmpMessageKind.DeleteObject or CwmpMessageKind.Reboot or CwmpMessageKind.FactoryReset
        or CwmpMessageKind.Download or CwmpMessageKind.ScheduleInform;

    public static bool IsResponse(this CwmpMessageKind k) => k.ToString().EndsWith("Response", StringComparison.Ordinal);

    /// <summary>Short display names used in summaries (GPV, SPV, ...).</summary>
    public static string ShortName(this CwmpMessageKind k) => k switch
    {
        CwmpMessageKind.GetParameterValues => "GPV",
        CwmpMessageKind.GetParameterValuesResponse => "GPVResponse",
        CwmpMessageKind.SetParameterValues => "SPV",
        CwmpMessageKind.SetParameterValuesResponse => "SPVResponse",
        CwmpMessageKind.GetParameterNames => "GPN",
        CwmpMessageKind.GetParameterNamesResponse => "GPNResponse",
        CwmpMessageKind.GetParameterAttributes => "GPA",
        CwmpMessageKind.GetParameterAttributesResponse => "GPAResponse",
        CwmpMessageKind.SetParameterAttributes => "SPA",
        CwmpMessageKind.SetParameterAttributesResponse => "SPAResponse",
        CwmpMessageKind.GetRPCMethods => "GetRPCMethods",
        CwmpMessageKind.GetRPCMethodsResponse => "GetRPCMethodsResponse",
        CwmpMessageKind.NotXml => "Not XML",
        CwmpMessageKind.NotSoap => "Not SOAP",
        _ => k.ToString(),
    };
}
