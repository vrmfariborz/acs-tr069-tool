using System.Diagnostics.CodeAnalysis;

namespace AcsTool.PacketCapture.Files;

/// <summary>
/// Pull-based reader of a capture file. <see cref="Sections"/> and <see cref="Interfaces"/> grow as blocks are read.
/// Not thread-safe.
/// </summary>
public interface ICaptureFileReader : IDisposable
{
    CaptureFileFormat Format { get; }

    IReadOnlyList<CaptureFileSection> Sections { get; }

    IReadOnlyList<CaptureFileInterface> Interfaces { get; }

    /// <summary>Byte offset of the next block/record.</summary>
    long Position { get; }

    /// <summary>
    /// Reads the next frame; false at a clean end of file. Throws <see cref="CaptureFileException"/> (with the failing block's
    /// offset) for corrupt or truncated input; frames returned before stay valid.
    /// </summary>
    bool TryReadNext([NotNullWhen(true)] out CaptureFileFrame? frame);
}
