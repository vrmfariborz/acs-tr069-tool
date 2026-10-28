namespace AcsTool.Core.Cwmp;

/// <summary>CWMP fault codes (Appendix B.2).</summary>
public static class FaultCodes
{
    public const uint MethodNotSupported = 9000;
    public const uint RequestDenied = 9001;
    public const uint InternalError = 9002;
    public const uint InvalidArguments = 9003;
    public const uint ResourcesExceeded = 9004;
    public const uint InvalidParameterName = 9005;
    public const uint InvalidParameterType = 9006;
    public const uint InvalidParameterValue = 9007;
    public const uint NonWritableParameter = 9008;
    public const uint NotificationRequestRejected = 9009;
    public const uint DownloadFailure = 9010;

    public const uint AcsMethodNotSupported = 8000;
    public const uint AcsRequestDenied = 8001;
    public const uint AcsInternalError = 8002;
    public const uint AcsInvalidArguments = 8003;
    public const uint AcsResourcesExceeded = 8004;
    public const uint AcsRetryRequest = 8005;

    private static readonly Dictionary<uint, string> Strings = new()
    {
        [9000] = "Method not supported",
        [9001] = "Request denied",
        [9002] = "Internal error",
        [9003] = "Invalid arguments",
        [9004] = "Resources exceeded",
        [9005] = "Invalid parameter name",
        [9006] = "Invalid parameter type",
        [9007] = "Invalid parameter value",
        [9008] = "Attempt to set a non-writable parameter",
        [9009] = "Notification request rejected",
        [9010] = "Download failure",
        [9011] = "Upload failure",
        [9012] = "File transfer server authentication failure",
        [9013] = "Unsupported protocol for file transfer",
        [9014] = "Download failure: unable to join multicast group",
        [9015] = "Download failure: unable to contact file server",
        [9016] = "Download failure: unable to access file",
        [9017] = "Download failure: unable to complete download",
        [9018] = "Download failure: file corrupted",
        [9019] = "Download failure: file authentication failure",
        [8000] = "Method not supported",
        [8001] = "Request denied",
        [8002] = "Internal error",
        [8003] = "Invalid arguments",
        [8004] = "Resources exceeded",
        [8005] = "Retry request",
    };

    public static string DefaultString(uint code) => Strings.TryGetValue(code, out var s) ? s : "Fault " + code;

    /// <summary>SOAP faultcode "Client" for 9001, 9003, 9005–9009, 9012–9019; "Server" otherwise.</summary>
    public static bool IsClientFault(uint code) =>
        code is 9001 or 9003 or (>= 9005 and <= 9009) or (>= 9012 and <= 9019);

    public static string SoapFaultCodeFor(uint code) => IsClientFault(code) ? "Client" : "Server";
}
