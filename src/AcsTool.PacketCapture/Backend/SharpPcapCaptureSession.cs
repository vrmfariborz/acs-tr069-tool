using SharpPcap;
using SharpPcap.LibPcap;

namespace AcsTool.PacketCapture.Backend;

/// <summary>
/// One open <see cref="LibPcapLiveDevice"/> session. The capture callback only copies bytes and takes no lock (PT-1):
/// it reads the frame delegate with <see cref="Volatile.Read{T}(ref T)"/>. <c>_lifecycle</c> serialises
/// Start/Stop/Dispose only; the capture thread never touches it, so <see cref="Stop"/> can join the capture thread.
/// </summary>
internal sealed class SharpPcapCaptureSession : ICaptureSession
{
    private readonly LibPcapLiveDevice _device;
    private readonly object _lifecycle = new();
    private Action<RawFrame>? _onFrame;
    private bool _started;
    private bool _disposed;
    private long _callbackFailures;

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

    /// <summary>Diagnostics: frames whose copy or consumer callback threw (they are dropped; capture keeps running).</summary>
    internal long CallbackFailures => Interlocked.Read(ref _callbackFailures);

    public void Start(Action<RawFrame> onFrame)
    {
        ArgumentNullException.ThrowIfNull(onFrame);
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
            if (_started)
                return;
            Volatile.Write(ref _onFrame, onFrame);
            _device.OnPacketArrival += OnPacketArrival;
            try
            {
                _device.StartCapture();
            }
            catch (Exception ex)
            {
                // Leave no handler behind: a retried Start must not deliver every frame twice (review m1).
                _device.OnPacketArrival -= OnPacketArrival;
                Volatile.Write(ref _onFrame, null);
                throw new CaptureOpenException(
                    $"Could not start capturing on '{Device.FriendlyName}': {ex.Message}",
                    CaptureBackendStatus.Error,
                    ex);
            }

            _started = true;
        }
    }

    public void Stop()
    {
        lock (_lifecycle)
        {
            if (!_started || Volatile.Read(ref _disposed))
                return;
            StopCore();
        }
    }

    public CaptureCounters ReadCounters()
    {
        // No lifecycle lock: a poll must not wait behind a Stop that is joining the capture thread, and the capture thread
        // never waits on this. A race with Dispose closing the handle surfaces as an exception and yields default counters.
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
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

    public void Dispose()
    {
        lock (_lifecycle)
        {
            if (_disposed)
                return;
            Volatile.Write(ref _disposed, true);
            try
            {
                if (_started)
                {
                    try
                    {
                        StopCore();
                    }
#pragma warning disable CA1031 // Dispose must not throw.
                    catch (Exception)
#pragma warning restore CA1031
                    {
                    }
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
        }
    }

    /// <summary>Caller holds <c>_lifecycle</c>. Clears the delegate first so in-flight packets become no-ops, then joins.</summary>
    private void StopCore()
    {
        Volatile.Write(ref _onFrame, null);
        try
        {
            // Waits up to StopCaptureTimeout for the capture thread (set in SharpPcapBackend.Open, review m4). The capture
            // thread never contends on our lock, so under traffic it exits as soon as libpcap's loop sees the break.
            if (_device.Started)
                _device.StopCapture();
        }
        finally
        {
            _device.OnPacketArrival -= OnPacketArrival;
            _started = false;
        }
    }

    private void OnPacketArrival(object sender, SharpPcap.PacketCapture e)
    {
        // PT-1: copy + stamp only. Lock-free (volatile read of the delegate), no logging or decoding, no exception escapes.
        var callback = Volatile.Read(ref _onFrame);
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
            Interlocked.Increment(ref _callbackFailures);
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
