namespace AcsTool.PacketCapture.Backend;

/// <summary>
/// Live-capture backend (design §5.2). PT0.2 has only <see cref="Probe"/>; device listing and sessions arrive with PT1.1.
/// Implementations must not load the native library on construction, only when a member is called.
/// </summary>
public interface ICaptureBackend
{
    /// <summary>Checks whether live capture is possible. Never throws.</summary>
    CaptureBackendInfo Probe();
}
