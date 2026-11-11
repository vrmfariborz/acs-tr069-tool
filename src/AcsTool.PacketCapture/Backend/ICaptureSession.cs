namespace AcsTool.PacketCapture.Backend;

/// <summary>
/// An open live-capture session on one device (design §5.2).
/// <see cref="Start"/> delivers frames on the backend's capture thread — the callback must stay O(copy) (PT-1).
/// </summary>
public interface ICaptureSession : IDisposable
{
    int InterfaceId { get; }
    LinkKind LinkType { get; }
    CaptureDeviceInfo Device { get; }

    /// <summary>Starts capture. Idempotent: a second call is a no-op while running. <paramref name="onFrame"/> runs on the capture thread.</summary>
    /// <remarks>
    /// <paramref name="onFrame"/> must not call <see cref="Stop"/> or <see cref="IDisposable.Dispose"/> on this session:
    /// the capture thread cannot join itself, so such a call returns only after the backend's StopCapture timeout
    /// (≈750 ms), and any concurrent outside <see cref="Stop"/> waits as long. Signal another thread to stop instead.
    /// </remarks>
    void Start(Action<RawFrame> onFrame);

    /// <summary>Stops capture and joins the capture thread. Idempotent. Must not be called from the <c>onFrame</c> callback (see <see cref="Start"/>).</summary>
    void Stop();

    CaptureCounters ReadCounters();
}
