namespace AcsTool.PacketCapture.Backend;

/// <summary>
/// Live-capture backend (design §5.2). Implementations must not load the native library on construction, only when a member is called.
/// </summary>
public interface ICaptureBackend
{
    /// <summary>Checks whether live capture is possible. Never throws.</summary>
    CaptureBackendInfo Probe();

    /// <summary>Lists capture devices. Throws <see cref="CaptureOpenException"/> with a user-facing message on failure (e.g. not installed).</summary>
    IReadOnlyList<CaptureDeviceInfo> ListDevices();

    /// <summary>Opens one device for capture. Throws <see cref="CaptureOpenException"/> on failure (bad filter, access denied, missing device).</summary>
    ICaptureSession Open(CaptureDeviceInfo device, CaptureOpenOptions options);
}
