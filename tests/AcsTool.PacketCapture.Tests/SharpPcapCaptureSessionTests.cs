using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Reflection;
using AcsTool.PacketCapture.Backend;
using SharpPcap;
using SharpPcap.LibPcap;

namespace AcsTool.PacketCapture.Tests;

/// <summary>Native-free tests of <see cref="SharpPcapCaptureSession"/> and <see cref="SharpPcapBackend.Open"/> via <see cref="TestLiveDevice"/>.</summary>
public class SharpPcapCaptureSessionTests
{
    private static readonly CaptureDeviceInfo Info = new("test0", "Test", null, [], false, true, null);

    private static SharpPcapCaptureSession NewSession(TestLiveDevice device, int interfaceId = 3) =>
        new(device, Info, interfaceId, LinkKind.Ethernet);

    private static void WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
            Thread.Sleep(5);
        Assert.True(condition(), "condition not reached within 5 s");
    }

    [Fact]
    public void Stop_UnderTraffic_JoinsCaptureThread_AndNoCallbacksAfter()
    {
        // Review M1: the capture callback used to take the same lock Stop held across StopCapture, so a busy capture thread
        // could never finish and every Stop ran into StopCaptureTimeout without joining.
        var device = new TestLiveDevice { AutoTraffic = true, StopCaptureTimeout = TimeSpan.FromSeconds(3) };
        using var session = NewSession(device);
        long received = 0;
        session.Start(_ => Interlocked.Increment(ref received));
        WaitUntil(() => Interlocked.Read(ref received) > 1000);

        var sw = Stopwatch.StartNew();
        session.Stop();
        sw.Stop();

        Assert.True(device.LastStopJoined, "Stop must join the capture thread, not time out");
        Assert.False(device.LastCaptureThread!.IsAlive);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1.5), $"Stop took {sw.Elapsed.TotalMilliseconds} ms");
        Assert.False(device.Started);

        var atStop = Interlocked.Read(ref received);
        Thread.Sleep(50);
        Assert.Equal(atStop, Interlocked.Read(ref received));
    }

    [Fact]
    public void Dispose_UnderTraffic_JoinsCaptureThread_AndCloses()
    {
        var device = new TestLiveDevice { AutoTraffic = true, StopCaptureTimeout = TimeSpan.FromSeconds(3) };
        var session = NewSession(device);
        long received = 0;
        session.Start(_ => Interlocked.Increment(ref received));
        WaitUntil(() => Interlocked.Read(ref received) > 1000);

        session.Dispose();
        session.Dispose(); // idempotent

        Assert.True(device.LastStopJoined);
        Assert.False(device.LastCaptureThread!.IsAlive);
        Assert.False(device.Opened);
        Assert.Throws<ObjectDisposedException>(() => session.Start(_ => { }));
        Assert.Throws<ObjectDisposedException>(() => session.ReadCounters());
        session.Stop(); // no-op after Dispose
    }

    [Fact]
    public void Stop_Then_Start_Again_Delivers()
    {
        var device = new TestLiveDevice { AutoTraffic = true, StopCaptureTimeout = TimeSpan.FromSeconds(3) };
        using var session = NewSession(device);
        long first = 0, second = 0;
        session.Start(_ => Interlocked.Increment(ref first));
        WaitUntil(() => Interlocked.Read(ref first) > 10);
        session.Stop();
        var firstAtStop = Interlocked.Read(ref first);

        session.Start(_ => Interlocked.Increment(ref second));
        WaitUntil(() => Interlocked.Read(ref second) > 10);
        session.Stop();

        Assert.Equal(firstAtStop, Interlocked.Read(ref first));
        Assert.True(device.LastStopJoined);
    }

    [Fact]
    public void Start_Twice_IsIdempotent()
    {
        var device = new TestLiveDevice();
        using var session = NewSession(device);
        var got = new List<RawFrame>();
        session.Start(got.Add);
        session.Start(_ => throw new InvalidOperationException("second Start must be ignored"));

        device.Emit(new PcapHeader(1, 0, 4, 4), [1, 2, 3, 4]);

        Assert.Single(got);
        Assert.Equal(1, device.StartCalls);
        Assert.Equal(0, session.CallbackFailures);
    }

    [Fact]
    public void Start_Failure_DetachesHandler_WrapsException_AndRetryDeliversOnce()
    {
        // Review m1: a failed StartCapture used to leave the handler attached, so a retried Start delivered every frame twice.
        var inner = new InvalidOperationException("not ready");
        var device = new TestLiveDevice { StartFailure = inner };
        using var session = NewSession(device);
        var got = new List<RawFrame>();

        var ex = Assert.Throws<CaptureOpenException>(() => session.Start(got.Add));
        Assert.Equal(CaptureBackendStatus.Error, ex.Status);
        Assert.Same(inner, ex.InnerException);

        device.Emit(new PcapHeader(1, 0, 1, 1), [9]);
        Assert.Empty(got); // no handler left behind

        device.StartFailure = null;
        session.Start(got.Add);
        device.Emit(new PcapHeader(1, 0, 1, 1), [9]);
        Assert.Single(got);
    }

    [Fact]
    public void Frame_IsExactCopy_WithOriginalLength_Timestamp_AndInterfaceId()
    {
        var device = new TestLiveDevice();
        using var session = NewSession(device, interfaceId: 5);
        RawFrame? frame = null;
        session.Start(f => frame = f);

        var buffer = new byte[] { 0xde, 0xad, 0xbe, 0xef };
        device.Emit(new PcapHeader(1_700_000_000, 123_456, packetLength: 1500, captureLength: 4), buffer);
        buffer[0] = 0; // SharpPcap reuses its buffer; the frame must own its bytes

        Assert.NotNull(frame);
        var f = frame.Value;
        Assert.Equal(5, f.InterfaceId);
        Assert.Equal(1500, f.OriginalLength);
        Assert.Equal(new byte[] { 0xde, 0xad, 0xbe, 0xef }, f.Data.ToArray());
        var expected = DateTime.UnixEpoch.AddSeconds(1_700_000_000).Ticks + (123_456 * 10);
        Assert.Equal(expected, f.TimestampTicksUtc);
    }

    [Fact]
    public void Frame_Empty_IsEmptyArray()
    {
        var device = new TestLiveDevice();
        using var session = NewSession(device);
        RawFrame? frame = null;
        session.Start(f => frame = f);

        device.Emit(new PcapHeader(1, 0, 0, 0), []);

        Assert.NotNull(frame);
        Assert.Equal(0, frame.Value.Data.Length);
    }

    [Fact]
    public void CallbackException_IsCounted_AndCaptureContinues()
    {
        var device = new TestLiveDevice();
        using var session = NewSession(device);
        var calls = 0;
        session.Start(_ =>
        {
            if (++calls == 1)
                throw new InvalidOperationException("boom");
        });

        device.Emit(new PcapHeader(1, 0, 1, 1), [1]);
        device.Emit(new PcapHeader(1, 0, 1, 1), [2]);

        Assert.Equal(2, calls);
        Assert.Equal(1, session.CallbackFailures);
    }

    [Fact]
    public void ReadCounters_MapsStatistics_AndFailureIsDefault()
    {
        var device = new TestLiveDevice { Stats = new StubStats { ReceivedPackets = 10, DroppedPackets = 2, InterfaceDroppedPackets = 1 } };
        using var session = NewSession(device);

        Assert.Equal(new CaptureCounters(10, 2, 1), session.ReadCounters());

        device.StatsFailure = new InvalidOperationException("closed");
        Assert.Equal(default, session.ReadCounters());
    }

    [Theory]
    [InlineData(1_700_000_000UL, 0UL, TimestampResolution.Microsecond, 0L)]
    [InlineData(1_700_000_000UL, 1UL, TimestampResolution.Microsecond, 10L)]
    [InlineData(1_700_000_000UL, 999_999UL, TimestampResolution.Microsecond, 9_999_990L)]
    [InlineData(1_700_000_000UL, 100UL, TimestampResolution.Nanosecond, 1L)]
    [InlineData(1_700_000_000UL, 150UL, TimestampResolution.Nanosecond, 2L)] // 1.5 ticks rounds away from zero
    [InlineData(1_700_000_000UL, 149UL, TimestampResolution.Nanosecond, 1L)]
    [InlineData(0UL, 0UL, TimestampResolution.Microsecond, 0L)]
    public void ToUtcTicks_UnixEpoch_And_Resolution(ulong seconds, ulong fraction, TimestampResolution resolution, long fractionTicks)
    {
        var tv = new PosixTimeval(seconds, fraction, resolution);

        var ticks = SharpPcapCaptureSession.ToUtcTicks(tv);

        Assert.Equal(DateTime.UnixEpoch.Ticks + ((long)seconds * TimeSpan.TicksPerSecond) + fractionTicks, ticks);
    }

    // ---- SharpPcapBackend.Open through the device seam ----

    private static SharpPcapBackend BackendFor(TestLiveDevice? device) =>
        new(() => 1, () => "test", name => device is not null && device.Name == name ? device : null);

    private static CaptureDeviceInfo InfoFor(string name) => new(name, name, null, [], false, true, null);

    [Fact]
    public void Open_PassesConfiguration_AndRaisesStopCaptureTimeout()
    {
        var device = new TestLiveDevice(opened: false);
        var options = new CaptureOpenOptions(Snaplen: 1234, Promiscuous: true, KernelBufferBytes: 4096, InterfaceId: 9);

        using var session = BackendFor(device).Open(InfoFor("test0"), options);

        var cfg = device.LastConfiguration!;
        Assert.Equal(1234, cfg.Snaplen);
        Assert.Equal(DeviceModes.Promiscuous, cfg.Mode);
        Assert.Equal(100, cfg.ReadTimeout);
        Assert.Equal(4096, cfg.KernelBufferSize);
        Assert.Equal(TimestampResolution.Nanosecond, cfg.TimestampResolution);
        // Review m4: Open's own 2 × ReadTimeout (200 ms) is below SharpPcap's 500 ms Unix poll.
        Assert.Equal(TimeSpan.FromMilliseconds(750), device.StopCaptureTimeout);
        Assert.Equal(9, session.InterfaceId);
        Assert.Equal(LinkKind.Ethernet, session.LinkType);
        Assert.Equal("test0", session.Device.Name);
    }

    [Fact]
    public void Open_DefaultsWithoutKernelBuffer_MicrosecondTimestamps()
    {
        var device = new TestLiveDevice(opened: false);
        var options = new CaptureOpenOptions(KernelBufferBytes: 0, PreferNanosecondTimestamps: false, Promiscuous: false);

        using var session = BackendFor(device).Open(InfoFor("test0"), options);

        Assert.Null(device.LastConfiguration!.KernelBufferSize);
        Assert.Equal(TimestampResolution.Microsecond, device.LastConfiguration.TimestampResolution);
        Assert.Equal(DeviceModes.None, device.LastConfiguration.Mode);
    }

    [Theory]
    [InlineData(100, 750)]
    [InlineData(375, 750)]
    [InlineData(500, 1000)]
    [InlineData(0, 750)]
    public void StopCaptureTimeoutFor_IsAtLeast750ms_OrTwiceReadTimeout(int readTimeoutMs, int expectedMs)
    {
        var timeout = SharpPcapBackend.StopCaptureTimeoutFor(new CaptureOpenOptions(ReadTimeoutMilliseconds: readTimeoutMs));

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), timeout);
    }

    [Fact]
    public void Open_BadFilter_ClosesDevice_AndThrowsWithText()
    {
        var device = new TestLiveDevice(opened: false) { FilterFailure = new InvalidOperationException("syntax error near 'tcpx'") };

        var ex = Assert.Throws<CaptureOpenException>(() =>
            BackendFor(device).Open(InfoFor("test0"), new CaptureOpenOptions(BpfFilter: "tcpx port 80")));

        Assert.Equal(CaptureBackendStatus.Error, ex.Status);
        Assert.Contains("syntax error near 'tcpx'", ex.Message, StringComparison.Ordinal);
        Assert.False(device.Opened);
        Assert.True(device.CloseCalls >= 1);
    }

    [Fact]
    public void Open_DeviceGone_IsError()
    {
        var ex = Assert.Throws<CaptureOpenException>(() => BackendFor(null).Open(InfoFor("gone0"), CaptureOpenOptions.Default));

        Assert.Equal(CaptureBackendStatus.Error, ex.Status);
        Assert.Contains("gone0", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_NativeMissing_IsNotInstalled_AndDeviceDisposed()
    {
        var device = new TestLiveDevice(opened: false) { OpenFailure = new DllNotFoundException("wpcap") };

        var ex = Assert.Throws<CaptureOpenException>(() => BackendFor(device).Open(InfoFor("test0"), CaptureOpenOptions.Default));

        Assert.Equal(CaptureBackendStatus.NotInstalled, ex.Status);
        Assert.True(device.CloseCalls >= 1); // Dispose → Close
    }

    [Theory]
    [InlineData(PcapError.PermissionDenied, CaptureBackendStatus.AccessDenied)]
    [InlineData(PcapError.PromiscuousPermissionDenied, CaptureBackendStatus.AccessDenied)]
    [InlineData(PcapError.NoSuchDevice, CaptureBackendStatus.Error)]
    public void Classify_UsesPcapErrorCode(PcapError error, CaptureBackendStatus expected)
    {
        // PcapException's constructors are internal; review n1 asks to classify by Error, not by message text.
        var ctor = typeof(PcapException).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, [typeof(string), typeof(PcapError)])!;
        var pcapEx = (PcapException)ctor.Invoke(["open failed", error]);

        Assert.Equal(expected, SharpPcapBackend.Classify(pcapEx).Status);
    }

    [Theory]
    [InlineData(0x0u, false, true)] // driver sets no flags → treated as up
    [InlineData(0x2u, false, true)] // UP
    [InlineData(0x4u, false, false)] // RUNNING without UP
    [InlineData(0x3u, true, true)] // LOOPBACK | UP
    public void ToInfo_MapsFlags(uint flags, bool loopback, bool up)
    {
        var device = new TestLiveDevice("eth9") { FlagsValue = flags, DescriptionValue = "NIC 9" };

        var info = SharpPcapBackend.ToInfo(device);

        Assert.Equal("eth9", info.Name);
        Assert.Equal("NIC 9", info.FriendlyName);
        Assert.Equal(loopback, info.IsLoopback);
        Assert.Equal(up, info.IsUp);
        Assert.Empty(info.Addresses);
    }

    [Fact]
    public void ToInfo_LoopbackByName_AndMacFormatting()
    {
        var device = new TestLiveDevice("lo") { FlagsValue = 0x2, DescriptionValue = "", Mac = PhysicalAddress.Parse("00-1A-2B-3C-4D-5E") };

        var info = SharpPcapBackend.ToInfo(device);

        Assert.True(info.IsLoopback);
        Assert.Equal("lo", info.FriendlyName);
        Assert.Equal("00:1a:2b:3c:4d:5e", info.MacAddress);
    }
}
