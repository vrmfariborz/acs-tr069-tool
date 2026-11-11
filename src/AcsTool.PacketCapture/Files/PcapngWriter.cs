using System.Buffers;
using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using AcsTool.PacketCapture.Backend;

namespace AcsTool.PacketCapture.Files;

/// <summary>
/// Section options for <see cref="PcapngWriter"/>. Null <see cref="UserApplication"/> means <c>AcsTool &lt;version&gt;</c>;
/// null <see cref="Os"/> means <see cref="RuntimeInformation.OSDescription"/>. An empty string omits the option.
/// </summary>
public sealed record PcapngWriterOptions(string? UserApplication = null, string? Os = null, string? Hardware = null);

/// <summary>
/// One interface (IDB) for <see cref="PcapngWriter"/>: LINKTYPE value (see <see cref="LinkTypes"/>), snap length (0 = unlimited),
/// <c>if_name</c> (device name), <c>if_description</c> (friendly name) and <c>if_filter</c> (BPF string). Design §12.1.
/// </summary>
public sealed record PcapngInterface(int LinkType, int SnapLength = 262_144, string? Name = null, string? Description = null, string? Filter = null);

/// <summary>
/// Managed PCAPNG writer (design §12.1, D6): one Section Header Block (<c>shb_userappl</c>, <c>shb_os</c>), one Interface Description
/// Block per <see cref="AddInterface"/> with <c>if_tsresol</c> = 7 (100 ns, so <see cref="DateTime.Ticks"/> are stored exactly), and an
/// Enhanced Packet Block per frame with an optional <c>opt_comment</c>. Little-endian. Not thread-safe; no native library.
/// </summary>
public sealed class PcapngWriter : IDisposable
{
    private const int MaxOptionLength = ushort.MaxValue;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly List<PcapngInterface> _interfaces = [];
    private bool _disposed;

    public PcapngWriter(Stream stream, PcapngWriterOptions? options = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _leaveOpen = leaveOpen;
        options ??= new PcapngWriterOptions();
        WriteSectionHeader(
            options.UserApplication ?? DefaultUserApplication,
            options.Os ?? RuntimeInformation.OSDescription,
            options.Hardware);
    }

    /// <summary><c>AcsTool &lt;version&gt;</c> from the assembly's informational version, without the source-revision suffix.</summary>
    public static string DefaultUserApplication { get; } = "AcsTool " + ProductVersion();

    public IReadOnlyList<PcapngInterface> Interfaces => _interfaces;

    public long FramesWritten { get; private set; }

    /// <summary>Writes an Interface Description Block and returns its interface id (0, 1, …) for <see cref="WritePacket(int, long, int, ReadOnlySpan{byte}, string?)"/>.</summary>
    public int AddInterface(PcapngInterface iface)
    {
        ArgumentNullException.ThrowIfNull(iface);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(iface.LinkType, nameof(iface));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(iface.LinkType, ushort.MaxValue, nameof(iface));
        ArgumentOutOfRangeException.ThrowIfNegative(iface.SnapLength, nameof(iface));

        var name = OptionalUtf8(iface.Name, nameof(iface));
        var description = OptionalUtf8(iface.Description, nameof(iface));
        var filter = OptionalUtf8(iface.Filter, nameof(iface), prefixByte: true);

        var options = OptionsLength(name) + OptionsLength(description) + OptionLength(1) + OptionsLength(filter) + 4;
        var body = 8 + options;
        using var block = new BlockBuffer(PcapngBlocks.InterfaceDescription, body);
        var w = block.Body;
        BinaryPrimitives.WriteUInt16LittleEndian(w, (ushort)iface.LinkType);
        BinaryPrimitives.WriteUInt16LittleEndian(w[2..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(w[4..], (uint)iface.SnapLength);
        var at = 8;
        at = WriteOption(w, at, PcapngBlocks.IfName, name);
        at = WriteOption(w, at, PcapngBlocks.IfDescription, description);
        at = WriteOptionValue(w, at, PcapngBlocks.IfTsresol, [CaptureTimestamps.TicksResolution]);
        at = WriteOption(w, at, PcapngBlocks.IfFilter, filter);
        WriteEndOfOptions(w, at);
        block.WriteTo(_stream);

        _interfaces.Add(iface);
        return _interfaces.Count - 1;
    }

    /// <summary>
    /// Writes one Enhanced Packet Block. <paramref name="timestampTicksUtc"/> is UTC <see cref="DateTime.Ticks"/> (≥ 1970);
    /// <paramref name="originalLength"/> must be at least <c>data.Length</c>. <paramref name="comment"/> becomes <c>opt_comment</c>
    /// (null or empty: none) and must not hold secrets (design §6.1).
    /// </summary>
    public void WritePacket(int interfaceId, long timestampTicksUtc, int originalLength, ReadOnlySpan<byte> data, string? comment = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)interfaceId >= (uint)_interfaces.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(interfaceId), interfaceId, "Unknown interface id; call AddInterface first.");
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(originalLength, data.Length);
        var units = CaptureTimestamps.ToUnitsSinceEpoch(timestampTicksUtc, nameof(timestampTicksUtc));
        var commentBytes = OptionalUtf8(comment, nameof(comment));

        var options = commentBytes is null ? 0 : OptionsLength(commentBytes) + 4;
        var body = 20 + Pad4(data.Length) + options;
        using var block = new BlockBuffer(PcapngBlocks.EnhancedPacket, body);
        var w = block.Body;
        BinaryPrimitives.WriteUInt32LittleEndian(w, (uint)interfaceId);
        BinaryPrimitives.WriteUInt32LittleEndian(w[4..], (uint)(units >> 32));
        BinaryPrimitives.WriteUInt32LittleEndian(w[8..], (uint)units);
        BinaryPrimitives.WriteUInt32LittleEndian(w[12..], (uint)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(w[16..], (uint)originalLength);
        data.CopyTo(w[20..]);
        if (commentBytes is not null)
        {
            var at = WriteOption(w, 20 + Pad4(data.Length), PcapngBlocks.OptComment, commentBytes);
            WriteEndOfOptions(w, at);
        }
        block.WriteTo(_stream);
        FramesWritten++;
    }

    /// <summary>Writes a captured <see cref="RawFrame"/> on interface <see cref="RawFrame.InterfaceId"/>.</summary>
    public void WritePacket(in RawFrame frame, string? comment = null) =>
        WritePacket(frame.InterfaceId, frame.TimestampTicksUtc, frame.OriginalLength, frame.Data.Span, comment);

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

    // ------------------------------------------------------------------ section header

    private void WriteSectionHeader(string userApplication, string os, string? hardware)
    {
        var appl = OptionalUtf8(userApplication, nameof(userApplication));
        var osBytes = OptionalUtf8(os, nameof(os));
        var hw = OptionalUtf8(hardware, nameof(hardware));
        var any = appl is not null || osBytes is not null || hw is not null;
        var options = any ? OptionsLength(appl) + OptionsLength(osBytes) + OptionsLength(hw) + 4 : 0;

        using var block = new BlockBuffer(PcapngBlocks.SectionHeader, 16 + options);
        var w = block.Body;
        BinaryPrimitives.WriteUInt32LittleEndian(w, PcapngBlocks.ByteOrderMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(w[4..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(w[6..], 0);
        BinaryPrimitives.WriteInt64LittleEndian(w[8..], -1); // section length not specified (streaming)
        if (any)
        {
            var at = 16;
            at = WriteOption(w, at, PcapngBlocks.ShbUserAppl, appl);
            at = WriteOption(w, at, PcapngBlocks.ShbOs, osBytes);
            at = WriteOption(w, at, PcapngBlocks.ShbHardware, hw);
            WriteEndOfOptions(w, at);
        }
        block.WriteTo(_stream);
    }

    // ------------------------------------------------------------------ helpers

    private static string ProductVersion()
    {
        var v = typeof(PcapngWriter).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(PcapngWriter).Assembly.GetName().Version?.ToString()
            ?? "0.0.0";
        var plus = v.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? v[..plus] : v;
    }

    private static byte[]? OptionalUtf8(string? value, string paramName, bool prefixByte = false)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }
        var count = Encoding.UTF8.GetByteCount(value) + (prefixByte ? 1 : 0);
        if (count > MaxOptionLength)
        {
            throw new ArgumentException($"Value is {count} bytes; PCAPNG options hold at most {MaxOptionLength}.", paramName);
        }
        var bytes = new byte[count];
        // if_filter: first byte 0 = libpcap filter string.
        Encoding.UTF8.GetBytes(value, bytes.AsSpan(prefixByte ? 1 : 0));
        return bytes;
    }

    private static int Pad4(int length) => (length + 3) & ~3;

    private static int OptionLength(int valueLength) => 4 + Pad4(valueLength);

    private static int OptionsLength(byte[]? value) => value is null ? 0 : OptionLength(value.Length);

    private static int WriteOptionValue(Span<byte> w, int at, ushort code, ReadOnlySpan<byte> value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(w[at..], code);
        BinaryPrimitives.WriteUInt16LittleEndian(w[(at + 2)..], (ushort)value.Length);
        value.CopyTo(w[(at + 4)..]);
        return at + OptionLength(value.Length);
    }

    private static int WriteOption(Span<byte> w, int at, ushort code, byte[]? value) =>
        value is null ? at : WriteOptionValue(w, at, code, value);

    private static void WriteEndOfOptions(Span<byte> w, int at) => w.Slice(at, 4).Clear();

    /// <summary>A pooled, zeroed buffer holding one whole block: type, length, body, length.</summary>
    private readonly ref struct BlockBuffer
    {
        private readonly byte[] _array;
        private readonly int _length;

        public BlockBuffer(uint type, int bodyLength)
        {
            _length = PcapngBlocks.BlockOverhead + Pad4(bodyLength);
            _array = ArrayPool<byte>.Shared.Rent(_length);
            var all = _array.AsSpan(0, _length);
            all.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(all, type);
            BinaryPrimitives.WriteUInt32LittleEndian(all[4..], (uint)_length);
            BinaryPrimitives.WriteUInt32LittleEndian(all[^4..], (uint)_length);
        }

        public Span<byte> Body => _array.AsSpan(8, _length - PcapngBlocks.BlockOverhead);

        public void WriteTo(Stream stream) => stream.Write(_array, 0, _length);

        public void Dispose() => ArrayPool<byte>.Shared.Return(_array);
    }
}
