using System.Buffers.Binary;

namespace AcsTool.PacketCapture.Files;

/// <summary>Format detection and whole-file reading within a budget (design §7, §9).</summary>
public static class CaptureFileReader
{
    /// <summary>The format of a file starting with <paramref name="header"/> (at least 4 bytes), or null when not recognised.</summary>
    public static CaptureFileFormat? DetectFormat(ReadOnlySpan<byte> header)
    {
        if (header.Length < 4)
        {
            return null;
        }
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) == PcapngBlocks.SectionHeader)
        {
            return CaptureFileFormat.Pcapng;
        }
        var le = BinaryPrimitives.ReadUInt32LittleEndian(header);
        var be = BinaryPrimitives.ReadUInt32BigEndian(header);
        return le is PcapReader.MagicMicro or PcapReader.MagicNano || be is PcapReader.MagicMicro or PcapReader.MagicNano
            ? CaptureFileFormat.Pcap
            : null;
    }

    /// <summary>Detects the format from the first bytes (the stream need not be seekable) and opens the matching reader.</summary>
    public static ICaptureFileReader Open(Stream stream, CaptureReadLimits? limits = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var head = new byte[4];
        try
        {
            var n = 0;
            while (n < head.Length)
            {
                var r = stream.Read(head, n, head.Length - n);
                if (r == 0)
                {
                    break;
                }
                n += r;
            }
            var format = DetectFormat(head.AsSpan(0, n))
                ?? throw new CaptureFileException("Not a PCAP or PCAPNG file", 0);
            // The readers own the stream from here (and dispose it on a header failure unless leaveOpen).
            var source = new ByteSource(stream, leaveOpen, head);
            return format == CaptureFileFormat.Pcapng ? new PcapngReader(source, limits) : new PcapReader(source, limits);
        }
        catch (CaptureFileException) when (!leaveOpen)
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads every frame up to the budget. Throws <see cref="CaptureFileException"/> only when the file header is unusable;
    /// a corrupt or truncated later block ends reading with <see cref="CaptureFileContent.Error"/> set and earlier frames kept.
    /// </summary>
    public static CaptureFileContent ReadAll(Stream stream, CaptureReadLimits? limits = null, bool leaveOpen = false)
    {
        var budget = limits ?? CaptureReadLimits.Default;
        using var reader = Open(stream, budget, leaveOpen);
        var frames = new List<CaptureFileFrame>();
        long bytes = 0;
        var stopped = false;
        CaptureFileException? error = null;
        try
        {
            while (reader.TryReadNext(out var frame))
            {
                if (frames.Count >= budget.MaxFrames || bytes + frame.Data.Length > budget.MaxBytes)
                {
                    stopped = true;
                    break;
                }
                frames.Add(frame);
                bytes += frame.Data.Length;
            }
        }
        catch (CaptureFileException ex)
        {
            error = ex;
        }
        return new CaptureFileContent(reader.Format, [.. reader.Sections], [.. reader.Interfaces], frames, stopped, error);
    }

    /// <summary>Reads a file from disk; see <see cref="ReadAll(Stream, CaptureReadLimits?, bool)"/>.</summary>
    public static CaptureFileContent ReadAll(string path, CaptureReadLimits? limits = null)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        return ReadAll(stream, limits, leaveOpen: false);
    }
}
