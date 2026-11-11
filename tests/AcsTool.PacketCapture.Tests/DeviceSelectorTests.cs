using AcsTool.Core.Settings;
using AcsTool.PacketCapture.Backend;
using AcsTool.Testing;

namespace AcsTool.PacketCapture.Tests;

public class DeviceSelectorTests
{
    private static readonly CaptureDeviceInfo Eth1 = FakeCaptureBackend.Device(
        "eth1", "Ethernet", loopback: false, up: true, "192.168.1.10", "fe80::1");
    private static readonly CaptureDeviceInfo Eth2 = FakeCaptureBackend.Device(
        "eth2", "Ethernet 2", loopback: false, up: true, "10.0.0.5");
    private static readonly CaptureDeviceInfo EthDown = FakeCaptureBackend.Device(
        "eth-down", "Down NIC", loopback: false, up: false, "172.16.0.1");
    private static readonly CaptureDeviceInfo Lo = FakeCaptureBackend.Device(
        "lo", "Loopback", loopback: true, up: true, "127.0.0.1", "::1");
    private static readonly CaptureDeviceInfo NpfLoopback = FakeCaptureBackend.Device(
        @"\Device\NPF_Loopback", "Adapter for loopback traffic capture", loopback: true, up: true);

    private static readonly CaptureDeviceInfo[] All = [Eth1, Eth2, EthDown, Lo, NpfLoopback];

    [Fact]
    public void Auto_BindAny_SelectsAllUpNonLoopback_WithoutLoopbackWhenUpstreamRemote()
    {
        var proxy = new ProxySettings(BindAddress: "0.0.0.0", UpstreamUrl: "http://acs.example:7557");

        var selected = DeviceSelector.Auto(All, proxy);

        Assert.Equal(new[] { Eth1, Eth2 }, selected);
    }

    [Fact]
    public void Auto_BindIPv6Any_SameAsIPv4Any()
    {
        var proxy = new ProxySettings(BindAddress: "::", UpstreamUrl: "http://acs.example:7557");

        var selected = DeviceSelector.Auto(All, proxy);

        Assert.Equal(new[] { Eth1, Eth2 }, selected);
    }

    [Fact]
    public void Auto_BindSpecificIp_SelectsOwningNic_Only()
    {
        var proxy = new ProxySettings(BindAddress: "10.0.0.5", UpstreamUrl: "http://acs.example:7557");

        var selected = DeviceSelector.Auto(All, proxy);

        Assert.Equal(new[] { Eth2 }, selected);
    }

    [Fact]
    public void Auto_BindSpecificIp_NoOwner_ReturnsEmpty_WithoutLoopback()
    {
        var proxy = new ProxySettings(BindAddress: "203.0.113.9", UpstreamUrl: "http://acs.example:7557");

        var selected = DeviceSelector.Auto(All, proxy);

        Assert.Empty(selected);
    }

    [Fact]
    public void Auto_LocalhostUpstream_AddsLoopbackDevices()
    {
        var proxy = new ProxySettings(BindAddress: "0.0.0.0", UpstreamUrl: "http://localhost:7557");

        var selected = DeviceSelector.Auto(All, proxy);

        Assert.Equal(new[] { Eth1, Eth2, Lo, NpfLoopback }, selected);
    }

    [Fact]
    public void Auto_LoopbackIpUpstream_AddsLoopback()
    {
        var proxy = new ProxySettings(BindAddress: "192.168.1.10", UpstreamUrl: "http://127.0.0.1:7557");

        var selected = DeviceSelector.Auto(All, proxy);

        Assert.Contains(Eth1, selected);
        Assert.Contains(Lo, selected);
        Assert.Contains(NpfLoopback, selected);
        Assert.DoesNotContain(Eth2, selected);
    }

    [Fact]
    public void Auto_Ipv6LoopbackUpstream_AddsLoopback()
    {
        var proxy = new ProxySettings(BindAddress: "0.0.0.0", UpstreamUrl: "http://[::1]:7557");

        var selected = DeviceSelector.Auto(All, proxy);

        Assert.Contains(Lo, selected);
    }

    [Fact]
    public void Auto_SimulatorTargetLocal_AddsLoopback_EvenWhenUpstreamRemote()
    {
        var proxy = new ProxySettings(BindAddress: "0.0.0.0", UpstreamUrl: "http://acs.example:7557");

        var selected = DeviceSelector.Auto(All, proxy, simulatorTargets: ["http://127.0.0.1:7547"]);

        Assert.Contains(Lo, selected);
        Assert.Contains(Eth1, selected);
    }

    [Fact]
    public void Auto_SimulatorTargetOnLocalNic_AddsLoopback()
    {
        // Self-connection to this PC's own LAN IP appears on Loopback (design §11.2 / D10).
        var proxy = new ProxySettings(BindAddress: "0.0.0.0", UpstreamUrl: "http://acs.example:7557");

        var selected = DeviceSelector.Auto(All, proxy, simulatorTargets: ["http://192.168.1.10:7547"]);

        Assert.Contains(Lo, selected);
    }

    [Fact]
    public void Auto_RemoteSimulatorTargets_NoLoopback()
    {
        var proxy = new ProxySettings(BindAddress: "0.0.0.0", UpstreamUrl: "http://acs.example:7557");

        var selected = DeviceSelector.Auto(All, proxy, simulatorTargets: ["http://acs.example:80", "https://other:443"]);

        Assert.DoesNotContain(Lo, selected);
        Assert.DoesNotContain(NpfLoopback, selected);
    }

    [Fact]
    public void Auto_DownNicExcluded_EvenWhenBindAny()
    {
        var proxy = new ProxySettings(BindAddress: "0.0.0.0", UpstreamUrl: "http://acs.example:7557");

        var selected = DeviceSelector.Auto(All, proxy);

        Assert.DoesNotContain(EthDown, selected);
    }

    [Theory]
    [InlineData("http://localhost:7557")]
    [InlineData("http://127.0.0.1:7557")]
    [InlineData("http://[::1]/7557")]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:7547")]
    [InlineData("localhost:7547")]
    [InlineData("[::1]:7547")]
    [InlineData("[::1]")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("http://[::ffff:127.0.0.1]:7547/")]
    [InlineData("192.168.1.10:7547")]
    [InlineData("::ffff:192.168.1.10")]
    [InlineData("[fe80::1]:7547")]
    public void IsLocalTarget_RecognisesLoopbackForms(string target)
    {
        Assert.True(DeviceSelector.IsLocalTarget(target, All));
    }

    [Theory]
    [InlineData("http://acs.example:7557")]
    [InlineData("https://203.0.113.1")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("203.0.113.1:7547")]
    [InlineData("[2001:db8::1]:7547")]
    [InlineData("::ffff:203.0.113.1")]
    public void IsLocalTarget_RejectsRemoteOrEmpty(string? target)
    {
        Assert.False(DeviceSelector.IsLocalTarget(target, All));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public void Auto_BindLoopback_SelectsLoopbackDevices_Only(string bind)
    {
        // D10: the device that owns the bind address. Npcap's loopback adapter lists no addresses, so all loopback devices count.
        var proxy = new ProxySettings(BindAddress: bind, UpstreamUrl: "http://acs.example:7557");

        var selected = DeviceSelector.Auto(All, proxy);

        Assert.Equal(new[] { Lo, NpfLoopback }, selected);
    }

    [Fact]
    public void Auto_BindLoopback_LocalUpstream_NoDuplicates()
    {
        var proxy = new ProxySettings(BindAddress: "127.0.0.1", UpstreamUrl: "http://localhost:7557");

        var selected = DeviceSelector.Auto(All, proxy);

        Assert.Equal(new[] { Lo, NpfLoopback }, selected);
    }

    [Fact]
    public void Auto_BindIPv4Mapped_SelectsOwningNic()
    {
        var proxy = new ProxySettings(BindAddress: "::ffff:10.0.0.5", UpstreamUrl: "http://acs.example:7557");

        var selected = DeviceSelector.Auto(All, proxy);

        Assert.Equal(new[] { Eth2 }, selected);
    }

    [Fact]
    public void Auto_BindSpecificIp_OnDownNic_StillSelected()
    {
        // A specific bind names its NIC explicitly; the up flag only filters the 0.0.0.0 / :: case.
        var proxy = new ProxySettings(BindAddress: "172.16.0.1", UpstreamUrl: "http://acs.example:7557");

        var selected = DeviceSelector.Auto(All, proxy);

        Assert.Equal(new[] { EthDown }, selected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-ip")]
    public void Auto_BindMissingOrInvalid_TreatedAsAny(string? bind)
    {
        var proxy = new ProxySettings(BindAddress: bind!, UpstreamUrl: "http://acs.example:7557");

        var selected = DeviceSelector.Auto(All, proxy);

        Assert.Equal(new[] { Eth1, Eth2 }, selected);
    }

    [Fact]
    public void Auto_NoDevices_ReturnsEmpty()
    {
        var proxy = new ProxySettings(BindAddress: "0.0.0.0", UpstreamUrl: "http://localhost:7557");

        Assert.Empty(DeviceSelector.Auto([], proxy, simulatorTargets: ["http://127.0.0.1:7547"]));
    }
}
