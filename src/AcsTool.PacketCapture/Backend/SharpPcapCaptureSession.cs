using SharpPcap;
using SharpPcap.LibPcap;

namespace AcsTool.PacketCapture.Backend;

/// <summary>One open <see cref="LibPcapLiveDevice"/> session. The capture callback only copies bytes (PT-1).</summary>
internal sealed class SharpPcapCaptureSession : ICaptureSession
{
    private readonly LibPcapLiveDevice _device;
    private readonly object _gate = new();
    private Action<RawFrame>? _onFrame;
    private bool _started;
    private bool _disposed;

    public SharpPcapCaptureSession(LibPcapLiveDevice device, CaptureDeviceInfo info, int interfaceId, LinkKind linkType)
    {
        _device = device;
        Device = info;
        InterfaceId = interfaceId;
        LinkType = linkType;
    }

    public int InterfaceId { get; }
    public LinkKind LinkType { get; }
    public CaptureDeviceInfo Device { get; }

    public void Start(Action<RawFrame> onFrame)
    {
        ArgumentNullException.ThrowIfNull(onFrame);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
                return;
            _onFrame = onFrame;
            _device.OnPacketArrival += OnPacketArrival;
            _device.StartCapture();
            _started = true;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_started || _disposed)
                return;
            try
            {
                if (_device.Started)
                    _device.StopCapture();
            }
            finally
            {
                _device.OnPacketArrival -= OnPacketArrival;
                _onFrame = null;
                _started = false;
            }
        }
    }

    public CaptureCounters ReadCounters()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                var stats = _device.Statistics;
                if (stats is null)
                    return default;
                return new CaptureCounters(stats.ReceivedPackets, stats.DroppedPackets, stats.InterfaceDroppedPackets);
            }
#pragma warning disable CA1031 // Counters are best-effort; a failed poll must not stop capture.
            catch (Exception)
#pragma warning restore CA1031
            {
                return default;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            try
            {
                if (_started)
                {
                    try
                    {
                        if (_device.Started)
                            _device.StopCapture();
                    }
#pragma warning disable CA1031 // Dispose must not throw.
                    catch (Exception)
#pragma warning restore CA1031
                    {
                    }

                    _device.OnPacketArrival -= OnPacketArrival;
                    _onFrame = null;
                    _started = false;
                }

                if (_device.Opened)
                    _device.Close();
                _device.Dispose();
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
            }
            finally
            {
                _disposed = true;
            }
        }
    }

    private void OnPacketArrival(object sender, SharpPcap.PacketCapture e)
    {
        // PT-1: copy + stamp only. No logging, decoding, locks beyond reading the delegate, or exceptions escaping.
        Action<RawFrame>? callback;
        lock (_gate)
            callback = _onFrame;
        if (callback is null)
            return;

        try
        {
            var data = e.Data;
            var copy = data.IsEmpty ? Array.Empty<byte>() : data.ToArray();
            var header = e.Header;
            var original = header is PcapHeader ph ? (int)ph.PacketLength : copy.Length;
            var ticks = ToUtcTicks(header.Timeval);
            callback(new RawFrame(InterfaceId, ticks, original, copy));
        }
#pragma warning disable CA1031 // A throw from the capture thread would tear down SharpPcap's loop.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    internal static long ToUtcTicks(PosixTimeval timeval)
    {
        // Prefer Value (seconds as decimal) so nanosecond resolutions survive; fall back to Date.
        try
        {
            var ticks = (long)decimal.Round(timeval.Value * TimeSpan.TicksPerSecond, 0, MidpointRounding.AwayFromZero);
            return DateTime.UnixEpoch.Ticks + ticks;
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
            return timeval.Date.ToUniversalTime().Ticks;
        }
    }
}
