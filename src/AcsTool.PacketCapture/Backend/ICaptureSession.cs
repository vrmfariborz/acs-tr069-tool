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
    void Start(Action<RawFrame> onFrame);

    /// <summary>Stops capture and joins the capture thread. Idempotent.</summary>
    void Stop();

    CaptureCounters ReadCounters();
}
