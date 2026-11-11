namespace AcsTool.PacketCapture.Backend;

/// <summary>User-facing failure opening a capture device or listing devices (design §5.2). Never thrown by <see cref="ICaptureBackend.Probe"/>.</summary>
public sealed class CaptureOpenException : Exception
{
    public CaptureBackendStatus Status { get; }

    public CaptureOpenException(string message, CaptureBackendStatus status, Exception? innerException = null)
        : base(message, innerException)
    {
        Status = status;
    }
}
