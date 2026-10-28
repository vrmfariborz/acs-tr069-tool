using AcsTool.Core.Cwmp;
using AcsTool.Core.Model;
using AcsTool.Core.Net;

namespace AcsTool.Simulator.Profiles;

public enum DataModelRoot { InternetGatewayDevice, Device }

public sealed record CpeProfile(Guid Id, string Name, DeviceIdStruct DeviceId, string AcsUrl,
    string? Username, string? PasswordProtected, CwmpVersion Version, DataModelRoot Root,
    IReadOnlyList<ParameterDefinition> Parameters, IReadOnlyList<EventStruct> DefaultEvents,
    PeriodicInformSettings Periodic, RetryPolicy Retry, IReadOnlyDictionary<string, RpcBehavior> RpcBehaviors,
    string? RawInformTemplate, bool UseRawTemplate, bool SendInvalidTemplate, MultiInstanceSettings Instances,
    TlsOptions Tls, int RequestTimeoutSeconds = 30, uint MaxEnvelopes = 1, int SchemaVersion = 1)
{
    public string RootPrefix => Root == DataModelRoot.Device ? "Device." : "InternetGatewayDevice.";
}

public sealed record ParameterDefinition(string Name, string XsdType, string Value, bool Writable = true,
    bool IncludeInInform = false, int Notification = 0);

public sealed record PeriodicInformSettings(bool Enabled = false, uint IntervalSeconds = 300, DateTimeOffset? PhaseTime = null, double JitterPercent = 0);

public sealed record RetryPolicy(uint MinimumWaitSeconds = 5, uint IntervalMultiplier = 2000, int? MaxRetries = null);

public sealed record MultiInstanceSettings(int Count = 1, string SerialPattern = "{serial}", int StartIndex = 1, double StaggerSeconds = 5);

public enum RpcBehaviorKind { Auto, Fault, NoResponse }

public sealed record RpcBehavior(RpcBehaviorKind Kind = RpcBehaviorKind.Auto, uint FaultCode = 9000, int DelaySeconds = 0);
