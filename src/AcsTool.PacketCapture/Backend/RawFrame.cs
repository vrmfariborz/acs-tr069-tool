namespace AcsTool.PacketCapture.Backend;

/// <summary>
/// One captured frame as delivered by <see cref="ICaptureSession.Start"/> (design §5.2).
/// <paramref name="TimestampTicksUtc"/> is UTC ticks (same epoch as <see cref="DateTime.Ticks"/>).
/// <paramref name="Data"/> is a private copy of at most the snap length; <paramref name="OriginalLength"/> is the on-wire length.
/// </summary>
public readonly record struct RawFrame(int InterfaceId, long TimestampTicksUtc, int OriginalLength, ReadOnlyMemory<byte> Data);
