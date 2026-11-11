using System.Buffers.Binary;
using AcsTool.PacketCapture.Backend;

namespace AcsTool.PacketCapture.Files;

/// <summary>Timestamp precision of a classic PCAP file (selects the magic number).</summary>
public enum PcapTimestampPrecision
{
    /// <summary><c>0xA1B23C4D</c>: lossless for 100 ns ticks.</summary>
    Nanosecond,

    /// <summary><c>0xA1B2C3D4</c>: the most widely read; ticks are truncated to µs.</summary>
    Microsecond,
}

/// <summary>
/// Managed classic PCAP writer (design §12.1): one link type per file, little-endian, version 2.4. Not thread-safe; no native library.
/// </summary>
public sealed class PcapWriter : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly PcapTimestampPrecision _precision;
    private bool _disposed;

    public PcapWriter(Stream stream, int linkType, int snapLength = 262_144,
        PcapTimestampPrecision precision = PcapTimestampPrecision.Nanosecond, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(linkType);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(linkType, ushort.MaxValue);
        ArgumentOutOfRangeException.ThrowIfNegative(snapLength);
        _stream = stream;
        _leaveOpen = leaveOpen;
        _precision = precision;
        LinkType = linkType;

        Span<byte> h = stackalloc byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(h, precision == PcapTimestampPrecision.Nanosecond ? PcapReader.MagicNano : PcapReader.MagicMicro);
        BinaryPrimitives.WriteUInt16LittleEndian(h[4..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(h[6..], 4);
        BinaryPrimitives.WriteInt32LittleEndian(h[8..], 0);  // thiszone
        BinaryPrimitives.WriteUInt32LittleEndian(h[12..], 0); // sigfigs
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], (uint)snapLength);
        BinaryPrimitives.WriteUInt32LittleEndian(h[20..], (uint)linkType);
        _stream.Write(h);
    }

    public int LinkType { get; }

    public LinkKind Link => LinkTypes.ToLinkKind(LinkType);

    public long FramesWritten { get; private set; }

    /// <summary>Writes one record. <paramref name="timestampTicksUtc"/> must lie in 1970..2106 (32-bit seconds).</summary>
    public void WritePacket(long timestampTicksUtc, int originalLength, ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(originalLength, data.Length);
        var units = CaptureTimestamps.ToUnitsSinceEpoch(timestampTicksUtc, nameof(timestampTicksUtc));
        var seconds = units / TimeSpan.TicksPerSecond;
        if (seconds > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(timestampTicksUtc), timestampTicksUtc, "PCAP timestamps end in 2106.");
        }
        var remainder = units % TimeSpan.TicksPerSecond;
        var fraction = _precision == PcapTimestampPrecision.Nanosecond ? remainder * 100 : remainder / 10;

        Span<byte> h = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(h, (uint)seconds);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], (uint)fraction);
        BinaryPrimitives.WriteUInt32LittleEndian(h[8..], (uint)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(h[12..], (uint)originalLength);
        _stream.Write(h);
        _stream.Write(data);
        FramesWritten++;
    }

    /// <summary>Writes a captured <see cref="RawFrame"/> (its interface id is ignored: a PCAP file has one link).</summary>
    public void WritePacket(in RawFrame frame) => WritePacket(frame.TimestampTicksUtc, frame.OriginalLength, frame.Data.Span);

    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _stream.Flush();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_leaveOpen)
        {
            _stream.Flush();
        }
        else
        {
            _stream.Dispose();
        }
    }
}
