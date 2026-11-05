namespace AcsTool.PacketCapture.Backend;

/// <summary>Per-session counters from the capture driver (design §5.2 / §7). Polled via <see cref="ICaptureSession.ReadCounters"/>.</summary>
public readonly record struct CaptureCounters(long Received, long DroppedByKernel, long DroppedByInterface);
