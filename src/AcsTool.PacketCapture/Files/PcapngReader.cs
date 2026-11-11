using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace AcsTool.PacketCapture.Files;

/// <summary>
/// Managed PCAPNG reader (design §9): several sections (each with its own byte order and interface table), IDB options
/// (<c>if_name</c>, <c>if_description</c>, <c>if_filter</c>, <c>if_tsresol</c>, <c>if_tsoffset</c>), Enhanced, Simple and legacy
/// Obsolete Packet Blocks with <c>opt_comment</c>. Other block types (ISB, NRB, DSB, custom, unknown) are skipped.
/// </summary>
public sealed class PcapngReader : ICaptureFileReader
{
    private const int ShbMinLength = 28;

    private readonly ByteSource _src;
    private readonly CaptureReadLimits _limits;
    private readonly List<CaptureFileSection> _sections = [];
    private readonly List<CaptureFileInterface> _interfaces = [];
    private readonly List<CaptureFileInterface> _sectionInterfaces = [];
    private bool _bigEndian;

    public PcapngReader(Stream stream, CaptureReadLimits? limits = null, bool leaveOpen = false)
        : this(new ByteSource(stream, leaveOpen), limits)
    {
    }

    internal PcapngReader(ByteSource source, CaptureReadLimits? limits)
    {
        _src = source;
        _limits = limits ?? CaptureReadLimits.Default;
        try
        {
            Span<byte> h = stackalloc byte[8];
            if (_src.ReadFully(h) < 8)
            {
                throw new CaptureFileException("Truncated PCAPNG section header", 0);
            }
            if (BinaryPrimitives.ReadUInt32LittleEndian(h) != PcapngBlocks.SectionHeader)
            {
                throw new CaptureFileException("Not a PCAPNG file (first block is not a Section Header Block)", 0);
            }
            ReadSectionHeader(0, h[4..]);
        }
        catch
        {
            _src.Dispose();
            throw;
        }
    }

    public CaptureFileFormat Format => CaptureFileFormat.Pcapng;

    public IReadOnlyList<CaptureFileSection> Sections => _sections;

    public IReadOnlyList<CaptureFileInterface> Interfaces => _interfaces;

    public long Position => _src.Position;

    public bool TryReadNext([NotNullWhen(true)] out CaptureFileFrame? frame)
    {
        frame = null;
        Span<byte> h = stackalloc byte[8];
        while (true)
        {
            var start = _src.Position;
            var n = _src.ReadFully(h);
            if (n == 0)
            {
                return false;
            }
            if (n < 8)
            {
                throw new CaptureFileException("Truncated PCAPNG block header", start);
            }

            // The SHB type is a palindrome, so it reads the same in either byte order.
            if (BinaryPrimitives.ReadUInt32LittleEndian(h) == PcapngBlocks.SectionHeader)
            {
                ReadSectionHeader(start, h[4..]);
                continue;
            }

            var type = ReadU32(h);
            var length = ReadU32(h[4..]);
            ValidateLength(length, PcapngBlocks.BlockOverhead, start);
            var body = ReadBody(length, start);

            switch (type)
            {
                case PcapngBlocks.InterfaceDescription:
                    ParseInterface(body, start);
                    break;
                case PcapngBlocks.EnhancedPacket:
                    frame = ParseEnhanced(body, start);
                    return true;
                case PcapngBlocks.SimplePacket:
                    frame = ParseSimple(body, start);
                    return true;
                case PcapngBlocks.ObsoletePacket:
                    frame = ParseObsolete(body, start);
                    return true;
                default:
                    // ISB, NRB, DSB, custom and unknown blocks: skipped (their bodies are already consumed).
                    break;
            }
        }
    }

    public void Dispose() => _src.Dispose();

    // ------------------------------------------------------------------ blocks

    private void ReadSectionHeader(long start, ReadOnlySpan<byte> rawLength)
    {
        Span<byte> bom = stackalloc byte[4];
        if (_src.ReadFully(bom) < 4)
        {
            throw new CaptureFileException("Truncated PCAPNG section header", start);
        }
        if (BinaryPrimitives.ReadUInt32LittleEndian(bom) == PcapngBlocks.ByteOrderMagic)
        {
            _bigEndian = false;
        }
        else if (BinaryPrimitives.ReadUInt32BigEndian(bom) == PcapngBlocks.ByteOrderMagic)
        {
            _bigEndian = true;
        }
        else
        {
            throw new CaptureFileException("Corrupt PCAPNG section header (unknown byte-order magic)", start);
        }

        var length = ReadU32(rawLength);
        ValidateLength(length, ShbMinLength, start);
        // Body after the byte-order magic: version, section length, options, then the trailing length.
        var rest = new byte[length - 12];
        if (_src.ReadFully(rest) < rest.Length)
        {
            throw new CaptureFileException("Truncated PCAPNG section header", start);
        }
        if (ReadU32(rest.AsSpan(rest.Length - 4)) != length)
        {
            throw new CaptureFileException("Corrupt PCAPNG section header (trailing length mismatch)", start);
        }

        var major = ReadU16(rest);
        var minor = ReadU16(rest.AsSpan(2));
        if (major != 1)
        {
            throw new CaptureFileException($"Unsupported PCAPNG version {major}.{minor}", start);
        }

        string? hardware = null, os = null, userAppl = null, comment = null;
        var options = new OptionReader(rest.AsSpan(12, rest.Length - 16), _bigEndian, start);
        while (options.MoveNext(out var code, out var value))
        {
            switch (code)
            {
                case PcapngBlocks.OptComment: comment = Append(comment, Utf8(value)); break;
                case PcapngBlocks.ShbHardware: hardware = Utf8(value); break;
                case PcapngBlocks.ShbOs: os = Utf8(value); break;
                case PcapngBlocks.ShbUserAppl: userAppl = Utf8(value); break;
                default: break;
            }
        }

        _sections.Add(new CaptureFileSection(_sections.Count, _bigEndian, major, minor, userAppl, os, hardware, comment));
        _sectionInterfaces.Clear();
    }

    private void ParseInterface(ReadOnlySpan<byte> body, long start)
    {
        if (body.Length < 8)
        {
            throw new CaptureFileException("Corrupt PCAPNG interface block (too short)", start);
        }
        var linkType = ReadU16(body);
        var snapLength = (int)Math.Min(ReadU32(body[4..]), int.MaxValue);
        string? name = null, description = null, filter = null;
        var resolution = CaptureTimestamps.DefaultResolution;
        long offset = 0;

        var options = new OptionReader(body[8..], _bigEndian, start);
        while (options.MoveNext(out var code, out var value))
        {
            switch (code)
            {
                case PcapngBlocks.IfName: name = Utf8(value); break;
                case PcapngBlocks.IfDescription: description = Utf8(value); break;
                case PcapngBlocks.IfTsresol when value.Length >= 1: resolution = value[0]; break;
                case PcapngBlocks.IfTsoffset when value.Length >= 8:
                    offset = _bigEndian ? BinaryPrimitives.ReadInt64BigEndian(value) : BinaryPrimitives.ReadInt64LittleEndian(value);
                    break;
                case PcapngBlocks.IfFilter when value.Length >= 1 && value[0] == 0: filter = Utf8(value[1..]); break;
                default: break;
            }
        }
        if (!CaptureTimestamps.IsSupported(resolution))
        {
            throw new CaptureFileException($"Unsupported if_tsresol 0x{resolution:X2} in PCAPNG interface block", start);
        }

        var iface = new CaptureFileInterface(_interfaces.Count, _sections.Count - 1, _sectionInterfaces.Count, linkType, snapLength,
            resolution, offset, name, description, filter);
        _interfaces.Add(iface);
        _sectionInterfaces.Add(iface);
    }

    private CaptureFileFrame ParseEnhanced(ReadOnlySpan<byte> body, long start)
    {
        if (body.Length < 20)
        {
            throw new CaptureFileException("Corrupt PCAPNG enhanced packet block (too short)", start);
        }
        var iface = Interface(ReadU32(body), start);
        var units = ((ulong)ReadU32(body[4..]) << 32) | ReadU32(body[8..]);
        var captured = ReadU32(body[12..]);
        var original = ReadU32(body[16..]);
        var data = Data(body[20..], captured, start);
        var comment = Comments(body[Math.Min(20 + Padded(captured), body.Length)..], start);
        return new CaptureFileFrame(iface.Id, Ticks(units, iface), Clamp(original), data, comment);
    }

    private CaptureFileFrame ParseObsolete(ReadOnlySpan<byte> body, long start)
    {
        if (body.Length < 20)
        {
            throw new CaptureFileException("Corrupt PCAPNG packet block (too short)", start);
        }
        var iface = Interface(ReadU16(body), start);
        var units = ((ulong)ReadU32(body[4..]) << 32) | ReadU32(body[8..]);
        var captured = ReadU32(body[12..]);
        var original = ReadU32(body[16..]);
        var data = Data(body[20..], captured, start);
        var comment = Comments(body[Math.Min(20 + Padded(captured), body.Length)..], start);
        return new CaptureFileFrame(iface.Id, Ticks(units, iface), Clamp(original), data, comment);
    }

    private CaptureFileFrame ParseSimple(ReadOnlySpan<byte> body, long start)
    {
        if (body.Length < 4)
        {
            throw new CaptureFileException("Corrupt PCAPNG simple packet block (too short)", start);
        }
        var iface = Interface(0, start);
        var original = ReadU32(body);
        // The captured length is implied: the original length, capped by the block size and the interface snap length.
        var captured = Math.Min(original, (uint)(body.Length - 4));
        if (iface.SnapLength > 0)
        {
            captured = Math.Min(captured, (uint)iface.SnapLength);
        }
        var data = body.Slice(4, (int)captured).ToArray();
        return new CaptureFileFrame(iface.Id, DateTime.UnixEpoch.Ticks, Clamp(original), data, HasTimestamp: false);
    }

    // ------------------------------------------------------------------ helpers

    private void ValidateLength(uint length, int minimum, long start)
    {
        if (length < minimum || length % 4 != 0)
        {
            throw new CaptureFileException($"Corrupt PCAPNG block length {length}", start);
        }
        if (length > (uint)_limits.MaxBlockBytes)
        {
            throw new CaptureFileException($"Corrupt PCAPNG block: length {length} exceeds {_limits.MaxBlockBytes} bytes", start);
        }
    }

    /// <summary>Reads the block body and trailing length; returns the body (without the trailer).</summary>
    private byte[] ReadBody(uint length, long start)
    {
        var rest = new byte[length - 8];
        if (_src.ReadFully(rest) < rest.Length)
        {
            throw new CaptureFileException("Truncated PCAPNG block", start);
        }
        if (ReadU32(rest.AsSpan(rest.Length - 4)) != length)
        {
            throw new CaptureFileException("Corrupt PCAPNG block (trailing length mismatch)", start);
        }
        return rest[..^4];
    }

    private CaptureFileInterface Interface(uint localId, long start)
    {
        if (localId >= (uint)_sectionInterfaces.Count)
        {
            throw new CaptureFileException($"Corrupt PCAPNG packet block: interface {localId} is not defined in this section", start);
        }
        return _sectionInterfaces[(int)localId];
    }

    private static byte[] Data(ReadOnlySpan<byte> rest, uint captured, long start)
    {
        if (captured > (uint)rest.Length)
        {
            throw new CaptureFileException($"Corrupt PCAPNG packet block: captured length {captured} exceeds the block", start);
        }
        return rest[..(int)captured].ToArray();
    }

    private string? Comments(ReadOnlySpan<byte> options, long start)
    {
        string? comment = null;
        var reader = new OptionReader(options, _bigEndian, start);
        while (reader.MoveNext(out var code, out var value))
        {
            if (code == PcapngBlocks.OptComment)
            {
                comment = Append(comment, Utf8(value));
            }
        }
        return comment;
    }

    private static long Ticks(ulong units, CaptureFileInterface iface) =>
        CaptureTimestamps.ToTicksUtc(units, iface.TimestampResolution, iface.TimestampOffsetSeconds);

    private static int Padded(uint length) => (int)((length + 3) & ~3u);

    private static int Clamp(uint value) => (int)Math.Min(value, int.MaxValue);

    private static string Utf8(ReadOnlySpan<byte> value) => Encoding.UTF8.GetString(value).TrimEnd('\0');

    private static string Append(string? existing, string value) => existing is null ? value : existing + "\n" + value;

    private ushort ReadU16(ReadOnlySpan<byte> s) => _bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s);

    private uint ReadU32(ReadOnlySpan<byte> s) => _bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s);

    /// <summary>Walks a PCAPNG option list: code, length, value padded to 4 bytes; stops at <c>opt_endofopt</c> or the end.</summary>
    private ref struct OptionReader(ReadOnlySpan<byte> span, bool bigEndian, long blockOffset)
    {
        private ReadOnlySpan<byte> _rest = span;

        public bool MoveNext(out ushort code, out ReadOnlySpan<byte> value)
        {
            code = 0;
            value = default;
            if (_rest.Length < 4)
            {
                return false; // no options, or no opt_endofopt before the end of the block
            }
            code = bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(_rest) : BinaryPrimitives.ReadUInt16LittleEndian(_rest);
            var length = bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(_rest[2..]) : BinaryPrimitives.ReadUInt16LittleEndian(_rest[2..]);
            if (code == PcapngBlocks.OptEndOfOpt)
            {
                _rest = default;
                return false;
            }
            var padded = (length + 3) & ~3;
            if (4 + padded > _rest.Length)
            {
                throw new CaptureFileException($"Corrupt PCAPNG option {code}: length {length} runs past the block", blockOffset);
            }
            value = _rest.Slice(4, length);
            _rest = _rest[(4 + padded)..];
            return true;
        }
    }
}
