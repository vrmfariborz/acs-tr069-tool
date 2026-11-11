using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace AcsTool.PacketCapture.Files;

/// <summary>
/// Managed reader for classic libpcap files: µs (<c>0xA1B2C3D4</c>) and ns (<c>0xA1B23C4D</c>) magic, either byte order
/// (design §9). One interface (id 0) with the header's link type and snap length.
/// </summary>
public sealed class PcapReader : ICaptureFileReader
{
    internal const uint MagicMicro = 0xA1B2C3D4;
    internal const uint MagicNano = 0xA1B23C4D;
    private const int FileHeaderLength = 24;
    private const int RecordHeaderLength = 16;

    private readonly ByteSource _src;
    private readonly CaptureReadLimits _limits;
    private readonly bool _bigEndian;
    private readonly bool _nano;
    private readonly CaptureFileSection[] _sections;
    private readonly CaptureFileInterface[] _interfaces;

    public PcapReader(Stream stream, CaptureReadLimits? limits = null, bool leaveOpen = false)
        : this(new ByteSource(stream, leaveOpen), limits)
    {
    }

    internal PcapReader(ByteSource source, CaptureReadLimits? limits)
    {
        _src = source;
        _limits = limits ?? CaptureReadLimits.Default;
        try
        {
            Span<byte> h = stackalloc byte[FileHeaderLength];
            if (_src.ReadFully(h) < FileHeaderLength)
            {
                throw new CaptureFileException("Truncated PCAP file header", 0);
            }
            var le = BinaryPrimitives.ReadUInt32LittleEndian(h);
            var be = BinaryPrimitives.ReadUInt32BigEndian(h);
            if (le is MagicMicro or MagicNano)
            {
                _bigEndian = false;
                _nano = le == MagicNano;
            }
            else if (be is MagicMicro or MagicNano)
            {
                _bigEndian = true;
                _nano = be == MagicNano;
            }
            else
            {
                throw new CaptureFileException("Not a PCAP file (unknown magic number)", 0);
            }

            var major = ReadU16(h[4..]);
            var minor = ReadU16(h[6..]);
            if (major != 2)
            {
                throw new CaptureFileException($"Unsupported PCAP version {major}.{minor}", 0);
            }
            var snapLength = (int)Math.Min(ReadU32(h[16..]), int.MaxValue);
            // LinkType and additional information: the low 16 bits are the link type; the upper bits carry FCS info.
            var linkType = (int)(ReadU32(h[20..]) & 0xFFFF);

            _sections = [new CaptureFileSection(0, _bigEndian, major, minor)];
            _interfaces = [new CaptureFileInterface(0, 0, 0, linkType, snapLength, _nano ? (byte)9 : (byte)6)];
        }
        catch
        {
            _src.Dispose();
            throw;
        }
    }

    public CaptureFileFormat Format => CaptureFileFormat.Pcap;

    public IReadOnlyList<CaptureFileSection> Sections => _sections;

    public IReadOnlyList<CaptureFileInterface> Interfaces => _interfaces;

    public long Position => _src.Position;

    public bool TryReadNext([NotNullWhen(true)] out CaptureFileFrame? frame)
    {
        frame = null;
        var start = _src.Position;
        Span<byte> h = stackalloc byte[RecordHeaderLength];
        var n = _src.ReadFully(h);
        if (n == 0)
        {
            return false;
        }
        if (n < RecordHeaderLength)
        {
            throw new CaptureFileException("Truncated PCAP record header", start);
        }

        var seconds = ReadU32(h);
        var fraction = ReadU32(h[4..]);
        var included = ReadU32(h[8..]);
        var original = ReadU32(h[12..]);
        if (included > (uint)_limits.MaxBlockBytes)
        {
            throw new CaptureFileException($"Corrupt PCAP record: captured length {included} exceeds {_limits.MaxBlockBytes} bytes", start);
        }

        var data = new byte[included];
        if (_src.ReadFully(data) < data.Length)
        {
            throw new CaptureFileException("Truncated PCAP record data", start);
        }

        var units = _nano ? (seconds * 1_000_000_000UL) + fraction : (seconds * 1_000_000UL) + fraction;
        var ticks = CaptureTimestamps.ToTicksUtc(units, _nano ? (byte)9 : (byte)6, 0);
        frame = new CaptureFileFrame(0, ticks, (int)Math.Min(original, int.MaxValue), data);
        return true;
    }

    public void Dispose() => _src.Dispose();

    private ushort ReadU16(ReadOnlySpan<byte> s) => _bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s);

    private uint ReadU32(ReadOnlySpan<byte> s) => _bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s);
}
