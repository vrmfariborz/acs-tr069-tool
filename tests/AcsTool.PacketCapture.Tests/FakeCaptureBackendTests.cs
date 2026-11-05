using AcsTool.PacketCapture.Backend;
using AcsTool.Testing;

namespace AcsTool.PacketCapture.Tests;

public class FakeCaptureBackendTests
{
    [Fact]
    public void Probe_NeverThrows_ReflectsDevices()
    {
        var backend = new FakeCaptureBackend();
        backend.Devices.Add(FakeCaptureBackend.Device("lo", loopback: true));

        var info = backend.Probe();

        Assert.True(info.IsAvailable);
        Assert.Equal(1, info.DeviceCount);
    }

    [Fact]
    public void ListDevices_ReturnsConfigured_OrThrows()
    {
        var backend = new FakeCaptureBackend();
        backend.Devices.Add(FakeCaptureBackend.Device("eth0"));

        Assert.Single(backend.ListDevices());

        backend.ListDevicesFailure = new CaptureOpenException("no pcap", CaptureBackendStatus.NotInstalled);
        var ex = Assert.Throws<CaptureOpenException>(() => backend.ListDevices());
        Assert.Equal(CaptureBackendStatus.NotInstalled, ex.Status);
    }

    [Fact]
    public void Start_Stop_Idempotent_AndStopJoins()
    {
        var backend = new FakeCaptureBackend { FrameDelay = TimeSpan.FromMilliseconds(5) };
        var device = FakeCaptureBackend.Device("lo", loopback: true);
        backend.Devices.Add(device);
        backend.ScriptedFrames["lo"] =
        [
            new RawFrame(0, DateTime.UtcNow.Ticks, 64, new byte[64]),
            new RawFrame(0, DateTime.UtcNow.Ticks, 64, new byte[64]),
            new RawFrame(0, DateTime.UtcNow.Ticks, 64, new byte[64]),
        ];

        using var session = backend.Open(device, new CaptureOpenOptions(InterfaceId: 7));
        var got = new List<RawFrame>();
        session.Start(f => { lock (got) got.Add(f); });
        session.Start(f => throw new InvalidOperationException("second start must be ignored")); // idempotent

        // Wait until at least one frame arrives, then stop (joins the feeder).
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            lock (got) { if (got.Count > 0) break; }
            Thread.Sleep(10);
        }

        session.Stop();
        var fake = Assert.IsType<FakeCaptureSession>(session);
        Assert.True(fake.LastStopJoined); // the feeder thread had exited when Stop returned
        int afterStop;
        lock (got) afterStop = got.Count;
        session.Stop(); // idempotent

        Thread.Sleep(50);
        lock (got)
        {
            Assert.NotEmpty(got);
            Assert.Equal(afterStop, got.Count); // no callbacks after Stop
            Assert.All(got, f => Assert.Equal(7, f.InterfaceId));
        }

        Assert.False(fake.IsStarted);
    }

    [Fact]
    public void ReadCounters_ReturnsConfigured()
    {
        var backend = new FakeCaptureBackend { Counters = new CaptureCounters(10, 2, 1) };
        var device = FakeCaptureBackend.Device("eth0");
        backend.Devices.Add(device);
        var session = backend.Open(device, CaptureOpenOptions.Default);

        Assert.Equal(new CaptureCounters(10, 2, 1), session.ReadCounters());
        backend.Counters = new CaptureCounters(20, 3, 1); // scripted change is visible to an open session
        Assert.Equal(new CaptureCounters(20, 3, 1), session.ReadCounters());

        session.Dispose();
        Assert.Throws<ObjectDisposedException>(() => session.ReadCounters());
    }

    [Fact]
    public void Open_UnknownDevice_Throws_LikeRealBackend()
    {
        var backend = new FakeCaptureBackend();
        backend.Devices.Add(FakeCaptureBackend.Device("eth0"));

        var ex = Assert.Throws<CaptureOpenException>(() => backend.Open(FakeCaptureBackend.Device("gone0"), CaptureOpenOptions.Default));
        Assert.Equal(CaptureBackendStatus.Error, ex.Status);
    }

    [Fact]
    public void Open_FailureModes()
    {
        var backend = new FakeCaptureBackend
        {
            OpenFailure = new CaptureOpenException("denied", CaptureBackendStatus.AccessDenied),
        };
        var device = FakeCaptureBackend.Device("eth0");

        var ex = Assert.Throws<CaptureOpenException>(() => backend.Open(device, CaptureOpenOptions.Default));
        Assert.Equal(CaptureBackendStatus.AccessDenied, ex.Status);
    }

    [Fact]
    public void CallbackException_DoesNotTearDownFeeder()
    {
        var backend = new FakeCaptureBackend();
        var device = FakeCaptureBackend.Device("lo", loopback: true);
        backend.Devices.Add(device);
        backend.ScriptedFrames["lo"] =
        [
            new RawFrame(0, 1, 1, new byte[] { 1 }),
            new RawFrame(0, 2, 1, new byte[] { 2 }),
        ];

        using var session = backend.Open(device, new CaptureOpenOptions(InterfaceId: 1));
        var count = 0;
        session.Start(_ =>
        {
            if (Interlocked.Increment(ref count) == 1)
                throw new InvalidOperationException("boom");
        });

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (Volatile.Read(ref count) < 2 && DateTime.UtcNow < deadline)
            Thread.Sleep(10);

        session.Stop();
        Assert.Equal(2, count);
    }
}
