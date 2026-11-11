using AcsTool.PacketCapture.Backend;

namespace AcsTool.PacketCapture.Tests;

/// <summary>
/// Opt-in live tests against real libpcap/Npcap. Not run in CI by default (D2); set <c>ACSTOOL_PCAP_LIVE=1</c>.
/// </summary>
public class SharpPcapLiveTests
{
    private static bool LiveEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("ACSTOOL_PCAP_LIVE"), "1", StringComparison.Ordinal);

    [SkippableFact]
    public void Live_ListDevices_OpenLoopback_StartStop()
    {
        Skip.IfNot(LiveEnabled, "Set ACSTOOL_PCAP_LIVE=1 to run live capture tests.");

        var backend = new SharpPcapBackend();
        var probe = backend.Probe();
        Skip.If(!probe.IsAvailable, probe.Message);

        var devices = backend.ListDevices();
        Assert.NotEmpty(devices);

        var loopback = devices.FirstOrDefault(d => d.IsLoopback) ?? devices[0];
        using var session = backend.Open(loopback, new CaptureOpenOptions(Snaplen: 256, InterfaceId: 1));
        Assert.Equal(1, session.InterfaceId);
        Assert.Equal(loopback.Name, session.Device.Name);

        var frames = 0;
        session.Start(_ => Interlocked.Increment(ref frames));
        // Brief capture window; we do not require frames (idle interface is fine).
        Thread.Sleep(200);
        session.Stop();
        _ = session.ReadCounters();
    }

    [SkippableFact]
    public void Live_BadFilter_ThrowsCaptureOpenException()
    {
        Skip.IfNot(LiveEnabled, "Set ACSTOOL_PCAP_LIVE=1 to run live capture tests.");

        var backend = new SharpPcapBackend();
        Skip.If(!backend.Probe().IsAvailable, "native pcap not available");

        var devices = backend.ListDevices();
        Skip.If(devices.Count == 0, "no devices");

        var ex = Assert.Throws<CaptureOpenException>(() =>
            backend.Open(devices[0], new CaptureOpenOptions(BpfFilter: "not a valid bpf ((")));
        Assert.Equal(CaptureBackendStatus.Error, ex.Status);
        Assert.Contains("filter", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
