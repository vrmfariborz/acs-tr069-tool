using AcsTool.Core.Cwmp;
using AcsTool.Core.Model;
using AcsTool.Core.Net;

namespace AcsTool.Simulator.Profiles;

/// <summary>Seed profiles from plan Appendix B.4.</summary>
public static class DefaultProfiles
{
    public const string DefaultAcsUrl = "http://127.0.0.1:7547/acs";

    /// <summary>Forced Inform parameters (relative to the root), in B.4 order.</summary>
    public static IReadOnlyList<string> ForcedRelative(DataModelRoot root) => root == DataModelRoot.Device
        ? ["RootDataModelVersion", "DeviceInfo.HardwareVersion", "DeviceInfo.SoftwareVersion", "DeviceInfo.ProvisioningCode",
           "ManagementServer.ParameterKey", "ManagementServer.ConnectionRequestURL", "ManagementServer.AliasBasedAddressing",
           "IP.Interface.1.IPv4Address.1.IPAddress"]
        : ["DeviceSummary", "DeviceInfo.SpecVersion", "DeviceInfo.HardwareVersion", "DeviceInfo.SoftwareVersion",
           "DeviceInfo.ProvisioningCode", "ManagementServer.ConnectionRequestURL", "ManagementServer.ParameterKey",
           "WANDevice.1.WANConnectionDevice.1.WANIPConnection.1.ExternalIPAddress"];

    public static IReadOnlyList<string> Forced(DataModelRoot root)
    {
        var prefix = root == DataModelRoot.Device ? "Device." : "InternetGatewayDevice.";
        return ForcedRelative(root).Select(r => prefix + r).ToList();
    }

    public static CpeProfile Tr098(string name = "TR-098 gateway", string serial = "SIM000001") =>
        Create(name, DataModelRoot.InternetGatewayDevice, new DeviceIdStruct("AcsTool", "00D09E", "IGD", serial));

    public static CpeProfile Tr181(string name = "TR-181 device", string serial = "SIM181001") =>
        Create(name, DataModelRoot.Device, new DeviceIdStruct("AcsTool", "00D09E", "Device2", serial));

    public static CpeProfile Create(string name, DataModelRoot root, DeviceIdStruct id) => new(
        Guid.NewGuid(), name, id, DefaultAcsUrl, null, null, CwmpVersion.V1_0, root, Seed(root, id),
        [new EventStruct(EventCodes.Boot)], new PeriodicInformSettings(), new RetryPolicy(),
        new Dictionary<string, RpcBehavior>(StringComparer.Ordinal), null, false, false, new MultiInstanceSettings(), TlsOptions.Default);

    public static IReadOnlyList<ParameterDefinition> Seed(DataModelRoot root, DeviceIdStruct id)
    {
        var p = root == DataModelRoot.Device ? "Device." : "InternetGatewayDevice.";
        const string S = XsdTypes.String, U = XsdTypes.UnsignedInt, B = XsdTypes.Boolean, D = XsdTypes.DateTime;
        var list = new List<ParameterDefinition>();
        void Add(string rel, string type, string value, bool writable, bool inform = false) =>
            list.Add(new ParameterDefinition(p + rel, type, value, writable, inform));

        if (root == DataModelRoot.Device)
        {
            Add("RootDataModelVersion", S, "2.15", false, true);
        }
        else
        {
            Add("DeviceSummary", S, "InternetGatewayDevice:1.4[](Baseline:1)", false, true);
        }
        Add("DeviceInfo.Manufacturer", S, id.Manufacturer, false);
        Add("DeviceInfo.ManufacturerOUI", S, id.OUI, false);
        Add("DeviceInfo.ProductClass", S, id.ProductClass, false);
        Add("DeviceInfo.SerialNumber", S, id.SerialNumber, false);
        if (root == DataModelRoot.InternetGatewayDevice)
        {
            Add("DeviceInfo.SpecVersion", S, "1.0", false, true);
        }
        Add("DeviceInfo.HardwareVersion", S, "HW-1.0", false, true);
        Add("DeviceInfo.SoftwareVersion", S, "1.0.0", false, true);
        Add("DeviceInfo.ProvisioningCode", S, "", true, true);
        Add("DeviceInfo.UpTime", U, "0", false);
        Add("ManagementServer.URL", S, DefaultAcsUrl, true);
        Add("ManagementServer.Username", S, "", true);
        Add("ManagementServer.ParameterKey", S, "", false, true);
        Add("ManagementServer.ConnectionRequestURL", S, "http://127.0.0.1:7548/cr", false, true);
        if (root == DataModelRoot.Device)
        {
            Add("ManagementServer.AliasBasedAddressing", B, "false", false, true);
        }
        Add("ManagementServer.PeriodicInformEnable", B, "true", true);
        Add("ManagementServer.PeriodicInformInterval", U, "300", true);
        Add("ManagementServer.PeriodicInformTime", D, "0001-01-01T00:00:00Z", true);
        Add("ManagementServer.CWMPRetryMinimumWaitInterval", U, "5", true);
        Add("ManagementServer.CWMPRetryIntervalMultiplier", U, "2000", true);
        if (root == DataModelRoot.Device)
        {
            Add("IP.Interface.1.IPv4Address.1.IPAddress", S, "192.0.2.10", false, true);
            Add("WiFi.SSID.1.SSID", S, "AcsTool-WiFi", true);
        }
        else
        {
            Add("WANDevice.1.WANConnectionDevice.1.WANIPConnection.1.ExternalIPAddress", S, "192.0.2.10", false, true);
            Add("LANDevice.1.WLANConfiguration.1.SSID", S, "AcsTool-WiFi", true);
        }
        Add("Time.NTPServer1", S, "pool.ntp.org", true);
        return list;
    }
}
