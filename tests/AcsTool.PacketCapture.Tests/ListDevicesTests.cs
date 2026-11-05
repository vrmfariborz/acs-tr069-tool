using System.Runtime.InteropServices;
using AcsTool.PacketCapture.Backend;

namespace AcsTool.PacketCapture.Tests;

public class ListDevicesTests
{
    [SkippableFact]
    public void ListDevices_WhenNativeMissing_ThrowsCaptureOpenException_NotInstalled()
    {
        // Only meaningful when the native library is absent (Windows CI). When libpcap is present, Probe covers Available.
        Skip.If(NativePcapLoadable(), "native pcap present; live ListDevices covered by SharpPcapLiveTests / Probe.");

        var backend = new SharpPcapBackend();
        var ex = Assert.Throws<CaptureOpenException>(() => backend.ListDevices());
        Assert.Equal(CaptureBackendStatus.NotInstalled, ex.Status);
        Assert.Contains(SharpPcapBackend.LibraryName, ex.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void ListDevices_WhenAvailable_ReturnsDevicesWithNames()
    {
        var backend = new SharpPcapBackend();
        var probe = backend.Probe();
        Skip.If(!probe.IsAvailable, probe.Message);

        var devices = backend.ListDevices();
        Assert.Equal(probe.DeviceCount, devices.Count);
        Assert.All(devices, d =>
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Name));
            Assert.False(string.IsNullOrWhiteSpace(d.FriendlyName));
            Assert.NotNull(d.Addresses);
        });
    }

    [Fact]
    public void LinkKindMapping_MapsKnownLayers()
    {
        Assert.Equal(LinkKind.Ethernet, LinkKindMapping.FromPacketDotNet(PacketDotNet.LinkLayers.Ethernet));
        Assert.Equal(LinkKind.Null, LinkKindMapping.FromPacketDotNet(PacketDotNet.LinkLayers.Null));
        Assert.Equal(LinkKind.Loop, LinkKindMapping.FromPacketDotNet(PacketDotNet.LinkLayers.Loop));
        Assert.Equal(LinkKind.LinuxSll, LinkKindMapping.FromPacketDotNet(PacketDotNet.LinkLayers.LinuxSll));
        Assert.Equal(LinkKind.LinuxSll2, LinkKindMapping.FromPacketDotNet(PacketDotNet.LinkLayers.LinuxSll2));
        Assert.Equal(LinkKind.Other, LinkKindMapping.FromPacketDotNet(PacketDotNet.LinkLayers.Ieee80211));
    }

    [Fact]
    public void CaptureOpenOptions_Defaults_MatchDesign()
    {
        var o = CaptureOpenOptions.Default;
        Assert.Equal(65_535, o.Snaplen);
        Assert.False(o.Promiscuous);
        Assert.Equal(16_777_216, o.KernelBufferBytes);
        Assert.Null(o.BpfFilter);
        Assert.Equal(100, o.ReadTimeoutMilliseconds);
        Assert.True(o.PreferNanosecondTimestamps);
    }

    private static bool NativePcapLoadable()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? [Path.Combine(Environment.SystemDirectory, "Npcap", "wpcap.dll"), "wpcap"]
            : ["libpcap.so", "libpcap.so.0", "libpcap.so.0.8", "libpcap.so.1", "libpcap.dylib", "wpcap"];
        foreach (var name in candidates)
        {
            if (NativeLibrary.TryLoad(name, out var handle))
            {
                NativeLibrary.Free(handle);
                return true;
            }
        }

        return false;
    }
}
