using AcsTool.PacketCapture.Backend;
using Microsoft.Extensions.DependencyInjection;
using PacketDotNet;

namespace AcsTool.PacketCapture.Tests;

public class SmokeTests
{
    [Fact]
    public void Assembly_Loads_HasName()
    {
        Assert.Equal("AcsTool.PacketCapture", AssemblyMarker.Name);
    }

    [Fact]
    public void AddAcsToolPacketCapture_RegistersSharpPcapBackendSingleton()
    {
        using var sp = new ServiceCollection().AddAcsToolPacketCapture().BuildServiceProvider();

        var first = sp.GetRequiredService<ICaptureBackend>();

        Assert.IsType<SharpPcapBackend>(first);
        Assert.Same(first, sp.GetRequiredService<ICaptureBackend>());
    }

    [Fact]
    public void PacketDotNet_BuildsAndReparsesTcpFrame_WithoutNativeLibrary()
    {
        var tcp = new TcpPacket(50000, 7547) { Synchronize = true };
        var ip = new IPv4Packet(System.Net.IPAddress.Parse("192.0.2.10"), System.Net.IPAddress.Parse("192.0.2.1")) { PayloadPacket = tcp };
        var eth = new EthernetPacket(
            System.Net.NetworkInformation.PhysicalAddress.Parse("02-00-00-00-00-01"),
            System.Net.NetworkInformation.PhysicalAddress.Parse("02-00-00-00-00-02"),
            EthernetType.IPv4) { PayloadPacket = ip };
        ip.UpdateCalculatedValues();
        tcp.UpdateCalculatedValues();

        var parsed = Packet.ParsePacket(LinkLayers.Ethernet, eth.Bytes).Extract<TcpPacket>();

        Assert.NotNull(parsed);
        Assert.Equal(7547, parsed.DestinationPort);
        Assert.True(parsed.Synchronize);
    }
}
