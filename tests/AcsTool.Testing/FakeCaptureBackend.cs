using System.Collections.Concurrent;
using System.Net;
using AcsTool.PacketCapture.Backend;

namespace AcsTool.Testing;

/// <summary>
/// Scripted <see cref="ICaptureBackend"/> for tests (design §5.2). No native library.
/// Configure <see cref="Devices"/>, per-device <see cref="ScriptedFrames"/>, optional open/list failures, and counters.
/// </summary>
public sealed class FakeCaptureBackend : ICaptureBackend
{
    private int _nextInterfaceId = 1;

    public CaptureBackendInfo ProbeResult { get; set; } =
        new(CaptureBackendStatus.Available, "fake-pcap 1.0", "Fake capture backend.", 0);

    public List<CaptureDeviceInfo> Devices { get; } = [];

    /// <summary>Frames delivered (in order) when a session for that device name is started.</summary>
    public Dictionary<string, List<RawFrame>> ScriptedFrames { get; } = new(StringComparer.Ordinal);

    /// <summary>When set, <see cref="ListDevices"/> throws this exception.</summary>
    public CaptureOpenException? ListDevicesFailure { get; set; }

    /// <summary>When set, every <see cref="Open"/> throws this exception.</summary>
    public CaptureOpenException? OpenFailure { get; set; }

    /// <summary>Per-device open failure (overrides <see cref="OpenFailure"/> for that name).</summary>
    public Dictionary<string, CaptureOpenException> OpenFailuresByDevice { get; } = new(StringComparer.Ordinal);

    /// <summary>Counters every open session reports; read on each <see cref="ICaptureSession.ReadCounters"/>, so tests can change them mid-run.</summary>
    public CaptureCounters Counters { get; set; }

    /// <summary>Delay between scripted frames on the feeder thread (default none).</summary>
    public TimeSpan FrameDelay { get; set; }

    public IReadOnlyList<FakeCaptureSession> Sessions => _sessions.ToArray();
    private readonly ConcurrentBag<FakeCaptureSession> _sessions = [];

    public CaptureBackendInfo Probe()
    {
        var devices = Devices.Count;
        return ProbeResult with { DeviceCount = ProbeResult.DeviceCount > 0 ? ProbeResult.DeviceCount : devices };
    }

    public IReadOnlyList<CaptureDeviceInfo> ListDevices()
    {
        if (ListDevicesFailure is not null)
            throw ListDevicesFailure;
        return Devices.ToList();
    }

    public ICaptureSession Open(CaptureDeviceInfo device, CaptureOpenOptions options)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(options);

        if (OpenFailuresByDevice.TryGetValue(device.Name, out var perDevice))
            throw perDevice;
        if (OpenFailure is not null)
            throw OpenFailure;
        // Like SharpPcapBackend.Open: a device that is not (or no longer) listed is an error.
        if (!Devices.Any(d => string.Equals(d.Name, device.Name, StringComparison.Ordinal)))
        {
            throw new CaptureOpenException(
                $"Capture device '{device.FriendlyName}' ({device.Name}) was not found. It may have been removed.",
                CaptureBackendStatus.Error);
        }

        var frames = ScriptedFrames.TryGetValue(device.Name, out var list) ? list : [];
        var interfaceId = options.InterfaceId != 0 ? options.InterfaceId : Interlocked.Increment(ref _nextInterfaceId);
        var session = new FakeCaptureSession(device, interfaceId, frames, () => Counters, FrameDelay);
        _sessions.Add(session);
        return session;
    }

    public static CaptureDeviceInfo Device(
        string name,
        string? friendly = null,
        bool loopback = false,
        bool up = true,
        params string[] addresses) =>
        new(
            name,
            friendly ?? name,
            Description: null,
            addresses.Select(IPAddress.Parse).ToList(),
            loopback,
            up,
            MacAddress: null);
}

/// <summary>One fake capture session. <see cref="Start"/> feeds scripted frames on a background thread; <see cref="Stop"/> joins it.</summary>
public sealed class FakeCaptureSession : ICaptureSession
{
    private readonly IReadOnlyList<RawFrame> _frames;
    private readonly Func<CaptureCounters> _counters;
    private readonly TimeSpan _frameDelay;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _stop = new(false);
    private Thread? _thread;
    private Action<RawFrame>? _onFrame;
    private bool _started;
    private bool _disposed;
    private int _delivered;

    public FakeCaptureSession(
        CaptureDeviceInfo device,
        int interfaceId,
        IReadOnlyList<RawFrame> frames,
        Func<CaptureCounters> counters,
        TimeSpan frameDelay)
    {
        Device = device;
        InterfaceId = interfaceId;
        LinkType = device.IsLoopback ? LinkKind.Null : LinkKind.Ethernet;
        _frames = frames;
        _counters = counters;
        _frameDelay = frameDelay;
    }

    public int InterfaceId { get; }
    public LinkKind LinkType { get; }
    public CaptureDeviceInfo Device { get; }
    public int Delivered => Volatile.Read(ref _delivered);

    /// <summary>True when the last <see cref="Stop"/> joined the feeder thread (it had exited when Stop returned).</summary>
    public bool LastStopJoined { get; private set; }
    public bool IsStarted
    {
        get { lock (_gate) return _started; }
    }

    public void Start(Action<RawFrame> onFrame)
    {
        ArgumentNullException.ThrowIfNull(onFrame);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
                return;
            Volatile.Write(ref _onFrame, onFrame);
            _stop.Reset();
            _thread = new Thread(Feed) { IsBackground = true, Name = $"FakeCapture:{Device.Name}" };
            _thread.Start();
            _started = true;
        }
    }

    public void Stop()
    {
        Thread? thread;
        lock (_gate)
        {
            if (!_started)
                return;
            _stop.Set();
            thread = _thread;
        }

        var joined = thread?.Join(TimeSpan.FromSeconds(5)) ?? true;
        lock (_gate)
        {
            _started = false;
            Volatile.Write(ref _onFrame, null);
            _thread = null;
            LastStopJoined = joined;
        }
    }

    public CaptureCounters ReadCounters()
    {
        lock (_gate)
            ObjectDisposedException.ThrowIf(_disposed, this);
        return _counters();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        Stop();
        _stop.Dispose();
    }

    private void Feed()
    {
        foreach (var frame in _frames)
        {
            if (_stop.IsSet)
                break;
            var cb = Volatile.Read(ref _onFrame); // lock-free, like the real capture callback (PT-1)
            if (cb is null)
                break;
            // Stamp the session's interface id so scripted frames need not know it in advance.
            var stamped = frame with { InterfaceId = InterfaceId };
            try
            {
                cb(stamped);
                Interlocked.Increment(ref _delivered);
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
            }

            if (_frameDelay > TimeSpan.Zero && _stop.Wait(_frameDelay))
                break;
        }

        // Keep the thread alive until Stop so Stop's join is meaningful even after frames are done.
        _stop.Wait();
    }
}
