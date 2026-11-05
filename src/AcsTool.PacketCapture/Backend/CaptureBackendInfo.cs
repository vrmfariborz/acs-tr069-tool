namespace AcsTool.PacketCapture.Backend;

/// <summary>Result of <see cref="ICaptureBackend.Probe"/> (design §5.2).</summary>
public enum CaptureBackendStatus
{
    /// <summary>The native capture library loaded and devices could be enumerated.</summary>
    Available,

    /// <summary>Npcap (Windows) or libpcap (Linux) is not installed: the native library could not be loaded.</summary>
    NotInstalled,

    /// <summary>The library loaded but the current user may not capture (e.g. Npcap installed "admin only").</summary>
    AccessDenied,

    /// <summary>Any other failure; see <see cref="CaptureBackendInfo.Message"/>.</summary>
    Error,
}

/// <summary>Outcome of a capture-backend probe. <paramref name="Version"/> is the native library version when known.</summary>
public sealed record CaptureBackendInfo(CaptureBackendStatus Status, string? Version, string Message, int DeviceCount)
{
    public bool IsAvailable => Status == CaptureBackendStatus.Available;
}
