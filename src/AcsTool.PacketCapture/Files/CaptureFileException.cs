namespace AcsTool.PacketCapture.Files;

/// <summary>
/// A capture file is not recognised, corrupt, or truncated. <see cref="Offset"/> is the byte offset of the block
/// (PCAPNG) or record (PCAP) where reading failed; 0 for the file header.
/// </summary>
public sealed class CaptureFileException : Exception
{
    public long Offset { get; }

    public CaptureFileException()
        : this("Invalid capture file.", 0)
    {
    }

    public CaptureFileException(string message)
        : this(message, 0)
    {
    }

    public CaptureFileException(string message, Exception innerException)
        : this(message, 0, innerException)
    {
    }

    public CaptureFileException(string message, long offset, Exception? innerException = null)
        : base($"{message} (at byte offset {offset})", innerException)
    {
        Offset = offset;
    }
}
