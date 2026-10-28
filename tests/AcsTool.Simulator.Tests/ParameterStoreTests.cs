using AcsTool.Core.Cwmp;
using AcsTool.Core.Model;
using AcsTool.Simulator.Parameters;
using AcsTool.Simulator.Profiles;

namespace AcsTool.Simulator.Tests;

public class ParameterStoreTests
{
    private const string R = "InternetGatewayDevice.";
    private readonly ParameterStore _store = new(DefaultProfiles.Tr098().Parameters);

    [Fact]
    public void GetValues_ExactAndPartial()
    {
        var r = _store.GetValues([R + "DeviceInfo.SoftwareVersion", R + "Time."]);
        Assert.False(r.IsFault);
        Assert.Equal(new[] { R + "DeviceInfo.SoftwareVersion", R + "Time.NTPServer1" }, r.Value!.Select(v => v.Name));
        Assert.Equal("1.0.0", r.Value![0].Value);
        Assert.Equal(_store.Count, _store.GetValues([""]).Value!.Count);
        var dev = _store.GetValues([R + "DeviceInfo."]).Value!;
        Assert.All(dev, v => Assert.StartsWith(R + "DeviceInfo.", v.Name, StringComparison.Ordinal));
        Assert.Equal(XsdTypes.UnsignedInt, dev.Single(v => v.Name.EndsWith("UpTime", StringComparison.Ordinal)).XsdType);
    }

    [Theory]
    [InlineData("InternetGatewayDevice.Nope")]
    [InlineData("InternetGatewayDevice.Nope.")]
    [InlineData("Device.")]
    public void GetValues_Unknown_9005(string name)
    {
        var r = _store.GetValues([R + "DeviceInfo.SoftwareVersion", name]);
        Assert.Equal(FaultCodes.InvalidParameterName, r.Fault!.FaultCode);
    }

    [Fact]
    public void GetNames_RootNextLevel_ReturnsRootObjectOnly()
    {
        var r = _store.GetNames("", true).Value!;
        Assert.Equal(new[] { R }, r.Select(x => x.Name));
    }

    [Fact]
    public void GetNames_ObjectNextLevel_DirectChildrenOnly()
    {
        var names = _store.GetNames(R, true).Value!.Select(x => x.Name).ToList();
        Assert.Contains(R + "DeviceSummary", names);
        Assert.Contains(R + "DeviceInfo.", names);
        Assert.Contains(R + "ManagementServer.", names);
        Assert.Contains(R + "WANDevice.", names);
        Assert.Contains(R + "Time.", names);
        Assert.DoesNotContain(names, n => n.Count(c => c == '.') > 2 || (n.Count(c => c == '.') == 2 && !n.EndsWith('.')));
        Assert.Equal(names.Distinct().Count(), names.Count);
        var writable = _store.GetNames(R + "Time.", true).Value!.Single();
        Assert.True(writable.Writable);
        Assert.False(_store.GetNames(R + "DeviceInfo.", true).Value!.Single(x => x.Name.EndsWith("SerialNumber", StringComparison.Ordinal)).Writable);
    }

    [Fact]
    public void GetNames_SubtreeAndLeafRules()
    {
        var all = _store.GetNames(R + "WANDevice.", false).Value!.Select(x => x.Name).ToList();
        Assert.Equal(new[]
        {
            R + "WANDevice.", R + "WANDevice.1.", R + "WANDevice.1.WANConnectionDevice.", R + "WANDevice.1.WANConnectionDevice.1.",
            R + "WANDevice.1.WANConnectionDevice.1.WANIPConnection.", R + "WANDevice.1.WANConnectionDevice.1.WANIPConnection.1.",
            R + "WANDevice.1.WANConnectionDevice.1.WANIPConnection.1.ExternalIPAddress",
        }, all);
        Assert.Single(_store.GetNames(R + "Time.NTPServer1", false).Value!);
        Assert.Equal(FaultCodes.InvalidArguments, _store.GetNames(R + "Time.NTPServer1", true).Fault!.FaultCode);
        Assert.Equal(FaultCodes.InvalidParameterName, _store.GetNames(R + "Nope.", true).Fault!.FaultCode);
        Assert.Equal(FaultCodes.InvalidParameterName, _store.GetNames("Device.", false).Fault!.FaultCode);
    }

    public static TheoryData<string, string, string, uint> SpvFaults() => new()
    {
        { R + "Nope", "x", XsdTypes.String, FaultCodes.InvalidParameterName },
        { R + "DeviceInfo.SerialNumber", "x", XsdTypes.String, FaultCodes.NonWritableParameter },
        { R + "ManagementServer.PeriodicInformInterval", "60", XsdTypes.String, FaultCodes.InvalidParameterType },
        { R + "ManagementServer.PeriodicInformInterval", "-5", XsdTypes.UnsignedInt, FaultCodes.InvalidParameterValue },
        { R + "ManagementServer.PeriodicInformEnable", "yes", XsdTypes.Boolean, FaultCodes.InvalidParameterValue },
    };

    [Theory]
    [MemberData(nameof(SpvFaults))]
    public void TrySetMany_Faults_AtomicNothingChanged(string name, string value, string type, uint code)
    {
        var before = _store.Snapshot();
        var r = _store.TrySetMany([new ParameterValue(R + "Time.NTPServer1", "ntp.example", XsdTypes.String), new ParameterValue(name, value, type)], "k1");
        Assert.Equal(FaultCodes.InvalidArguments, r.Fault!.FaultCode);
        var item = Assert.Single(r.Fault.SpvFaults);
        Assert.Equal(name, item.ParameterName);
        Assert.Equal(code, item.FaultCode);
        Assert.Equal(before, _store.Snapshot());
    }

    [Fact]
    public void TrySetMany_Success_CommitsAndSetsParameterKey()
    {
        var r = _store.TrySetMany([
            new ParameterValue(R + "Time.NTPServer1", "ntp.example"),
            new ParameterValue(R + "ManagementServer.PeriodicInformEnable", "0", XsdTypes.Boolean),
            new ParameterValue(R + "ManagementServer.PeriodicInformInterval", "60", XsdTypes.UnsignedInt),
        ], "key-42");
        Assert.Equal(0, r.Value);
        Assert.Equal("ntp.example", _store.Get(R + "Time.NTPServer1")!.Value);
        Assert.Equal("false", _store.Get(R + "ManagementServer.PeriodicInformEnable")!.Value);
        Assert.Equal("60", _store.Get(R + "ManagementServer.PeriodicInformInterval")!.Value);
        Assert.Equal("key-42", _store.Get(R + "ManagementServer.ParameterKey")!.Value);
        Assert.Empty(_store.TakeChangedForValueChange()); // ACS changes are not VALUE CHANGE
    }

    [Fact]
    public void LocalEdit_NotifiedParam_ReportedOnce()
    {
        var seed = DefaultProfiles.Tr098().Parameters.Select(p => p.Name.EndsWith("SSID", StringComparison.Ordinal) ? p with { Notification = 2 } : p);
        var store = new ParameterStore(seed);
        Assert.True(store.ApplyLocalEdit(R + "LANDevice.1.WLANConfiguration.1.SSID", "new"));
        Assert.False(store.ApplyLocalEdit(R + "Time.NTPServer1", "x")); // notification off
        var changed = store.TakeChangedForValueChange();
        Assert.Equal("new", Assert.Single(changed).Value);
        Assert.Empty(store.TakeChangedForValueChange());
        store.Reseed(DefaultProfiles.Tr098().Parameters);
        Assert.Equal("AcsTool-WiFi", store.Get(R + "LANDevice.1.WLANConfiguration.1.SSID")!.Value);
    }
}
