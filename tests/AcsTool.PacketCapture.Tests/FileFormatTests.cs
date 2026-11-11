using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using AcsTool.PacketCapture.Backend;
using AcsTool.PacketCapture.Files;

namespace AcsTool.PacketCapture.Tests;

/// <summary>
/// PT1.2: managed PCAP/PCAPNG reader and writer. Fixtures under <c>tests/fixtures/pcap/</c> come from
/// <c>make_fixtures.py</c> (independent struct-packed bytes, plus scapy-made files); the expected values here mirror that script.
/// </summary>
public class FileFormatTests
{
    private static readonly long Epoch = DateTime.UnixEpoch.Ticks;
    private const long Sec = TimeSpan.TicksPerSecond;
    private const long Base = 1_762_000_000L;

    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "pcap", name);

    private static byte[] Fixture(string name) => File.ReadAllBytes(FixturePath(name));

    private static byte[] Payload(int n, int seed)
    {
        var b = new byte[n];
        for (var k = 0; k < n; k++)
        {
            b[k] = (byte)((seed + k) & 0xFF);
        }
        return b;
    }

    private static CaptureFileContent ReadAll(byte[] bytes, CaptureReadLimits? limits = null)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        return CaptureFileReader.ReadAll(ms, limits);
    }

    /// <summary>A stream that hands out at most a few bytes per Read and cannot seek (like a pipe).</summary>
    private sealed class TrickleStream(byte[] data) : Stream
    {
        private int _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(Math.Min(count, 3), data.Length - _pos);
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ------------------------------------------------------------------ format detection

    [Theory]
    [InlineData(new byte[] { 0xD4, 0xC3, 0xB2, 0xA1 }, CaptureFileFormat.Pcap)]
    [InlineData(new byte[] { 0xA1, 0xB2, 0xC3, 0xD4 }, CaptureFileFormat.Pcap)]
    [InlineData(new byte[] { 0x4D, 0x3C, 0xB2, 0xA1 }, CaptureFileFormat.Pcap)]
    [InlineData(new byte[] { 0xA1, 0xB2, 0x3C, 0x4D }, CaptureFileFormat.Pcap)]
    [InlineData(new byte[] { 0x0A, 0x0D, 0x0D, 0x0A }, CaptureFileFormat.Pcapng)]
    public void DetectFormat_RecognisesMagics(byte[] head, CaptureFileFormat expected) =>
        Assert.Equal(expected, CaptureFileReader.DetectFormat(head));

    [Theory]
    [InlineData(new byte[] { 0x3C, 0x3F, 0x78, 0x6D })] // "<?xm"
    [InlineData(new byte[] { 0x34, 0xCD, 0xB2, 0xA1 })] // modified (Kuznetzov) pcap: unsupported
    [InlineData(new byte[] { 0x0A, 0x0D })]
    public void DetectFormat_Unknown_ReturnsNull(byte[] head) => Assert.Null(CaptureFileReader.DetectFormat(head));

    [Fact]
    public void Open_NotACaptureFile_ThrowsAtOffsetZero()
    {
        var ex = Assert.Throws<CaptureFileException>(() => ReadAll(Encoding.ASCII.GetBytes("<?xml version=\"1.0\"?><x/>")));
        Assert.Equal(0, ex.Offset);
    }

    [Fact]
    public void Open_EmptyStream_Throws()
    {
        var ex = Assert.Throws<CaptureFileException>(() => ReadAll([]));
        Assert.Equal(0, ex.Offset);
    }

    // ------------------------------------------------------------------ PCAP reader

    public static TheoryData<string, bool, bool> PcapFixtures => new()
    {
        { "us-le.pcap", false, false },
        { "us-be.pcap", true, false },
        { "ns-le.pcap", false, true },
        { "ns-be.pcap", true, true },
    };

    [Theory]
    [MemberData(nameof(PcapFixtures))]
    public void PcapReader_ReadsBothResolutionsAndByteOrders(string name, bool bigEndian, bool nano)
    {
        var c = ReadAll(Fixture(name));

        Assert.Equal(CaptureFileFormat.Pcap, c.Format);
        Assert.Null(c.Error);
        Assert.False(c.StoppedAtLimit);
        var section = Assert.Single(c.Sections);
        Assert.Equal(bigEndian, section.BigEndian);
        Assert.Equal(2, section.MajorVersion);
        Assert.Equal(4, section.MinorVersion);
        var iface = Assert.Single(c.Interfaces);
        Assert.Equal(0, iface.Id);
        Assert.Equal(LinkTypes.Ethernet, iface.LinkType);
        Assert.Equal(LinkKind.Ethernet, iface.Link);
        Assert.Equal(65535, iface.SnapLength);
        Assert.Equal(nano ? 9 : 6, iface.TimestampResolution);

        Assert.Equal(3, c.Frames.Count);
        long[] expected = nano
            ? [Epoch + Base * Sec + 1_234_568, Epoch + (Base + 1) * Sec + 10_000_000, Epoch + (Base + 2) * Sec]
            : [Epoch + Base * Sec + 1_234_560, Epoch + (Base + 1) * Sec + 9_999_990, Epoch + (Base + 2) * Sec];
        Assert.Equal(expected, c.Frames.Select(f => f.TimestampTicksUtc));
        Assert.All(c.Frames, f => Assert.True(f.HasTimestamp));
        Assert.All(c.Frames, f => Assert.Equal(0, f.InterfaceId));
        Assert.All(c.Frames, f => Assert.Null(f.Comment));
        Assert.Equal([60, 1514, 1], c.Frames.Select(f => f.OriginalLength));
        Assert.Equal(Payload(60, 1), c.Frames[0].Data);
        Assert.Equal(Payload(54, 2), c.Frames[1].Data);
        Assert.Equal(Payload(1, 3), c.Frames[2].Data);
    }

    [Theory]
    [InlineData("scapy-us.pcap", 6)]
    [InlineData("scapy-ns.pcap", 9)]
    public void PcapReader_ReadsScapyFiles(string name, int resolution)
    {
        var c = ReadAll(Fixture(name));

        Assert.Null(c.Error);
        Assert.Equal(resolution, Assert.Single(c.Interfaces).TimestampResolution);
        Assert.Equal(3, c.Frames.Count);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(Epoch + (Base + i) * Sec + 5_000_000, c.Frames[i].TimestampTicksUtc);
            Assert.Equal(71, c.Frames[i].Data.Length);
            Assert.Equal(71, c.Frames[i].OriginalLength);
            Assert.Equal(0x08, c.Frames[i].Data[12]); // EtherType IPv4
        }
    }

    [Fact]
    public void PcapReader_NonSeekableTrickleStream_Works()
    {
        using var s = new TrickleStream(Fixture("ns-be.pcap"));
        var c = CaptureFileReader.ReadAll(s);
        Assert.Null(c.Error);
        Assert.Equal(3, c.Frames.Count);
        Assert.Equal(Payload(54, 2), c.Frames[1].Data);
    }

    [Theory]
    [InlineData(10)] // inside the third record header
    [InlineData(1)]  // inside the third record's data
    public void PcapReader_TruncatedLastRecord_ReturnsEarlierFramesAndOffset(int cut)
    {
        var bytes = Fixture("ns-le.pcap");
        var c = ReadAll(bytes[..^cut]);

        Assert.Equal(2, c.Frames.Count);
        Assert.NotNull(c.Error);
        Assert.Equal(24 + (16 + 60) + (16 + 54), c.Error!.Offset);
        Assert.Contains("truncated", c.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PcapReader_TruncatedFileHeader_Throws()
    {
        var ex = Assert.Throws<CaptureFileException>(() => ReadAll(Fixture("us-le.pcap")[..20]));
        Assert.Equal(0, ex.Offset);
    }

    [Fact]
    public void PcapReader_OversizedRecord_IsCorruptAtRecordOffset()
    {
        var bytes = Fixture("us-le.pcap");
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24 + 76 + 8), 0x7FFF_FFFF); // second record incl_len
        var c = ReadAll(bytes);

        Assert.Single(c.Frames);
        Assert.Equal(24 + 76, c.Error!.Offset);
    }

    [Fact]
    public void PcapReader_UnsupportedMajorVersion_Throws()
    {
        var bytes = Fixture("us-le.pcap");
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 3);
        Assert.Throws<CaptureFileException>(() => ReadAll(bytes));
    }

    [Fact]
    public void PcapReader_StreamingApi_ThrowsAfterEarlierFrames()
    {
        var bytes = Fixture("us-be.pcap")[..^1];
        using var ms = new MemoryStream(bytes);
        using var reader = CaptureFileReader.Open(ms);
        Assert.IsType<PcapReader>(reader);
        Assert.True(reader.TryReadNext(out var f0));
        Assert.Equal(60, f0!.Data.Length);
        Assert.True(reader.TryReadNext(out _));
        var ex = Assert.Throws<CaptureFileException>(() => reader.TryReadNext(out _));
        Assert.Equal(170, ex.Offset);
    }

    [Fact]
    public void ReadAll_StopsAtFrameBudget()
    {
        var c = ReadAll(Fixture("us-le.pcap"), new CaptureReadLimits(MaxFrames: 2));
        Assert.Equal(2, c.Frames.Count);
        Assert.True(c.StoppedAtLimit);
        Assert.Null(c.Error);
    }

    [Fact]
    public void ReadAll_StopsAtByteBudget()
    {
        var c = ReadAll(Fixture("us-le.pcap"), new CaptureReadLimits(MaxBytes: 100));
        Assert.Single(c.Frames); // 60 + 54 > 100
        Assert.True(c.StoppedAtLimit);
    }

    [Fact]
    public void ReadAll_ExactlyAtBudget_DoesNotReportStop()
    {
        var c = ReadAll(Fixture("us-le.pcap"), new CaptureReadLimits(MaxFrames: 3, MaxBytes: 115));
        Assert.Equal(3, c.Frames.Count);
        Assert.False(c.StoppedAtLimit);
    }

    [Fact]
    public void ReadAll_FromPath_Works()
    {
        var c = CaptureFileReader.ReadAll(FixturePath("multi-section.pcapng"));
        Assert.Equal(CaptureFileFormat.Pcapng, c.Format);
        Assert.Equal(6, c.Frames.Count);
    }

    // ------------------------------------------------------------------ PCAPNG reader

    [Fact]
    public void PcapngReader_MultiSection_BothEndiannesses_AllPacketBlocks()
    {
        var c = ReadAll(Fixture("multi-section.pcapng"));

        Assert.Equal(CaptureFileFormat.Pcapng, c.Format);
        Assert.Null(c.Error);

        Assert.Equal(3, c.Sections.Count);
        Assert.Equal([false, true, false], c.Sections.Select(s => s.BigEndian));
        Assert.Equal("fixture-app 1.0", c.Sections[0].UserApplication);
        Assert.Equal("fixture-os", c.Sections[0].Os);
        Assert.Equal("fixture-hw", c.Sections[0].Hardware);
        Assert.Equal("section zero", c.Sections[0].Comment);
        Assert.Equal("fixture-be", c.Sections[1].UserApplication);
        Assert.Null(c.Sections[2].UserApplication);
        Assert.All(c.Sections, s => Assert.Equal(1, s.MajorVersion));

        Assert.Equal(4, c.Interfaces.Count);
        Assert.Equal([0, 1, 2, 3], c.Interfaces.Select(i => i.Id));
        Assert.Equal([0, 0, 1, 2], c.Interfaces.Select(i => i.SectionIndex));
        Assert.Equal([0, 1, 0, 0], c.Interfaces.Select(i => i.LocalId));
        Assert.Equal([LinkKind.Ethernet, LinkKind.Null, LinkKind.LinuxSll, LinkKind.Raw], c.Interfaces.Select(i => i.Link));
        Assert.Equal([6, 9, 0x94, 7], c.Interfaces.Select(i => (int)i.TimestampResolution));
        Assert.Equal([0, 262144, 65535, 0], c.Interfaces.Select(i => i.SnapLength));
        Assert.Equal("eth0", c.Interfaces[0].Name);
        Assert.Equal("Ethernet adapter", c.Interfaces[0].Description);
        Assert.Equal("lo", c.Interfaces[1].Name);
        Assert.Equal("tcp port 7547", c.Interfaces[1].Filter);
        Assert.Equal("any", c.Interfaces[2].Name);
        Assert.Equal(100, c.Interfaces[2].TimestampOffsetSeconds);
        Assert.Null(c.Interfaces[3].Name);

        Assert.Equal(6, c.Frames.Count);
        var f = c.Frames;
        // EPB if0, µs default resolution.
        Assert.Equal(0, f[0].InterfaceId);
        Assert.Equal(Epoch + Base * Sec + 2_500_000, f[0].TimestampTicksUtc);
        Assert.Equal(Payload(60, 10), f[0].Data);
        Assert.Equal("first comment", f[0].Comment);
        // EPB if1, ns, two comments joined; the unknown block before it is skipped.
        Assert.Equal(1, f[1].InterfaceId);
        Assert.Equal(Epoch + Base * Sec + 1_234_568, f[1].TimestampTicksUtc);
        Assert.Equal(Payload(44, 11), f[1].Data);
        Assert.Equal("line one\nline two", f[1].Comment);
        // SPB (no timestamp) on if0; ISB and NRB skipped.
        Assert.Equal(0, f[2].InterfaceId);
        Assert.False(f[2].HasTimestamp);
        Assert.Equal(Payload(33, 12), f[2].Data);
        Assert.Equal(33, f[2].OriginalLength);
        // Legacy OPB on if1.
        Assert.Equal(1, f[3].InterfaceId);
        Assert.Equal(Epoch + (Base + 1) * Sec, f[3].TimestampTicksUtc);
        Assert.Equal(Payload(20, 13), f[3].Data);
        Assert.Equal(40, f[3].OriginalLength);
        Assert.Equal("legacy", f[3].Comment);
        // Big-endian section: local id 0 → global 2; 2^-20 resolution, +100 s offset.
        Assert.Equal(2, f[4].InterfaceId);
        Assert.Equal(Epoch + (Base + 100) * Sec + 5_000_000, f[4].TimestampTicksUtc);
        Assert.Equal(Payload(28, 14), f[4].Data);
        Assert.Null(f[4].Comment);
        // Third section, 100 ns.
        Assert.Equal(3, f[5].InterfaceId);
        Assert.Equal(Epoch + (Base + 3) * Sec + 1_234_567, f[5].TimestampTicksUtc);
        Assert.Equal(Payload(20, 15), f[5].Data);
        Assert.All(f.Where(x => x != f[2]), x => Assert.True(x.HasTimestamp));
    }

    [Fact]
    public void PcapngReader_ReadsScapyFile()
    {
        var c = ReadAll(Fixture("scapy.pcapng"));
        Assert.Null(c.Error);
        Assert.Equal(LinkKind.Ethernet, Assert.Single(c.Interfaces).Link);
        Assert.Equal(3, c.Frames.Count);
        Assert.Equal(Epoch + (Base + 1) * Sec + 5_000_000, c.Frames[1].TimestampTicksUtc);
        Assert.Equal("scapy comment", c.Frames[1].Comment);
        Assert.Null(c.Frames[0].Comment);
        Assert.All(c.Frames, x => Assert.Equal(71, x.Data.Length));
    }

    [Fact]
    public void PcapngReader_NonSeekableTrickleStream_Works()
    {
        using var s = new TrickleStream(Fixture("multi-section.pcapng"));
        var c = CaptureFileReader.ReadAll(s);
        Assert.Null(c.Error);
        Assert.Equal(6, c.Frames.Count);
    }

    private static int LastBlockOffset(byte[] full) =>
        full.Length - (int)BinaryPrimitives.ReadUInt32LittleEndian(full.AsSpan(full.Length - 4));

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(30)]
    public void PcapngReader_TruncatedLastBlock_ReturnsEarlierFramesAndOffset(int cut)
    {
        var full = Fixture("multi-section.pcapng");
        var c = ReadAll(full[..^cut]);

        Assert.Equal(5, c.Frames.Count);
        Assert.NotNull(c.Error);
        Assert.Equal(LastBlockOffset(full), c.Error!.Offset);
        Assert.Contains("truncated", c.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PcapngReader_TrailerLengthMismatch_IsCorrupt()
    {
        var full = Fixture("multi-section.pcapng");
        var offset = LastBlockOffset(full);
        BinaryPrimitives.WriteUInt32LittleEndian(full.AsSpan(full.Length - 4), 4096);
        var c = ReadAll(full);

        Assert.Equal(5, c.Frames.Count);
        Assert.Equal(offset, c.Error!.Offset);
    }

    private static byte[] Block(uint type, byte[] body)
    {
        var padded = (body.Length + 3) & ~3;
        var b = new byte[12 + padded];
        BinaryPrimitives.WriteUInt32LittleEndian(b, type);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)b.Length);
        body.CopyTo(b, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(b.Length - 4), (uint)b.Length);
        return b;
    }

    private static byte[] MinimalShb()
    {
        var body = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(body, 0x1A2B3C4D);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), 1);
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(8), -1);
        return Block(0x0A0D0D0A, body);
    }

    private static byte[] MinimalIdb(ushort linkType = 1)
    {
        var body = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(body, linkType);
        return Block(1, body);
    }

    private static byte[] Epb(uint ifid, byte[] data, uint? capLenOverride = null)
    {
        var body = new byte[20 + data.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(body, ifid);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), capLenOverride ?? (uint)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), (uint)data.Length);
        data.CopyTo(body, 20);
        return Block(6, body);
    }

    [Fact]
    public void PcapngReader_EpbForUndefinedInterface_IsCorrupt()
    {
        var shb = MinimalShb();
        var idb = MinimalIdb();
        var good = Epb(0, [1, 2, 3]);
        byte[] bytes = [.. shb, .. idb, .. good, .. Epb(1, [4])];
        var c = ReadAll(bytes);

        Assert.Single(c.Frames);
        Assert.Equal(shb.Length + idb.Length + good.Length, c.Error!.Offset);
        Assert.Contains("interface", c.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PcapngReader_SimplePacket_CappedBySnapLength()
    {
        var idb = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(idb, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(idb.AsSpan(4), 4); // snaplen 4
        var spb = new byte[4 + 12];
        BinaryPrimitives.WriteUInt32LittleEndian(spb, 10);          // original length 10
        Payload(10, 40).CopyTo(spb, 4);
        byte[] bytes = [.. MinimalShb(), .. Block(1, idb), .. Block(3, spb)];
        var f = Assert.Single(ReadAll(bytes).Frames);
        Assert.Equal(Payload(4, 40), f.Data);
        Assert.Equal(10, f.OriginalLength);
        Assert.False(f.HasTimestamp);
    }

    [Fact]
    public void PcapngReader_InterfacesResetPerSection()
    {
        // Section 2 has no IDB, so its EPB for interface 0 is corrupt even though section 1 defined one.
        byte[] bytes = [.. MinimalShb(), .. MinimalIdb(), .. Epb(0, [1]), .. MinimalShb(), .. Epb(0, [2])];
        var c = ReadAll(bytes);
        Assert.Single(c.Frames);
        Assert.NotNull(c.Error);
    }

    [Fact]
    public void PcapngReader_CapturedLengthBeyondBlock_IsCorrupt()
    {
        byte[] bytes = [.. MinimalShb(), .. MinimalIdb(), .. Epb(0, [1, 2, 3, 4], capLenOverride: 400)];
        var c = ReadAll(bytes);
        Assert.Empty(c.Frames);
        Assert.Equal(MinimalShb().Length + MinimalIdb().Length, c.Error!.Offset);
    }

    [Theory]
    [InlineData(8u)]   // shorter than the 12-byte minimum
    [InlineData(18u)]  // not a multiple of 4
    public void PcapngReader_BadBlockLength_IsCorrupt(uint length)
    {
        var junk = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(junk, 0xBAD);
        BinaryPrimitives.WriteUInt32LittleEndian(junk.AsSpan(4), length);
        byte[] bytes = [.. MinimalShb(), .. junk];
        var c = ReadAll(bytes);
        Assert.Equal(MinimalShb().Length, c.Error!.Offset);
    }

    [Fact]
    public void PcapngReader_BlockOverMaxBlockBytes_IsCorrupt()
    {
        byte[] bytes = [.. MinimalShb(), .. MinimalIdb(), .. Epb(0, new byte[200])];
        var c = ReadAll(bytes, new CaptureReadLimits(MaxBlockBytes: 128));
        Assert.Empty(c.Frames);
        Assert.Contains("block", c.Error!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PcapngReader_UnknownByteOrderMagic_Throws()
    {
        var shb = MinimalShb();
        BinaryPrimitives.WriteUInt32LittleEndian(shb.AsSpan(8), 0x12345678);
        var ex = Assert.Throws<CaptureFileException>(() => ReadAll(shb));
        Assert.Equal(0, ex.Offset);
    }

    [Fact]
    public void PcapngReader_UnsupportedMajorVersion_IsCorrupt()
    {
        var second = MinimalShb();
        BinaryPrimitives.WriteUInt16LittleEndian(second.AsSpan(12), 2);
        byte[] bytes = [.. MinimalShb(), .. MinimalIdb(), .. Epb(0, [9]), .. second];
        var c = ReadAll(bytes);
        Assert.Single(c.Frames);
        Assert.Contains("version", c.Error!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PcapngReader_OptionRunningPastBlock_IsCorrupt()
    {
        var body = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(body, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(8), 2);    // if_name
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(10), 200); // length past the end
        byte[] bytes = [.. MinimalShb(), .. Block(1, body)];
        var c = ReadAll(bytes);
        Assert.Equal(MinimalShb().Length, c.Error!.Offset);
    }

    [Fact]
    public void PcapngReader_FileWithoutShb_Throws()
    {
        var ex = Assert.Throws<CaptureFileException>(() => ReadAll(MinimalIdb()));
        Assert.Equal(0, ex.Offset);
    }

    // ------------------------------------------------------------------ timestamp resolution

    [Theory]
    [InlineData(6, 1_000_001UL, 10_000_010L)]           // µs
    [InlineData(3, 1_500UL, 15_000_000L)]               // ms
    [InlineData(0, 2UL, 20_000_000L)]                   // s
    [InlineData(7, 123UL, 123L)]                        // 100 ns
    [InlineData(9, 150UL, 2L)]                          // ns, 1.5 ticks rounds away from zero
    [InlineData(9, 149UL, 1L)]
    [InlineData(12, 1_000_000UL, 10L)]                  // ps
    [InlineData(0x80 | 20, 1UL << 20, 10_000_000L)]     // 2^-20
    [InlineData(0x80 | 1, 3UL, 15_000_000L)]            // 2^-1
    [InlineData(0x80 | 0, 4UL, 40_000_000L)]            // 2^0
    public void TimestampUnits_ConvertToTicks(byte resolution, ulong units, long expectedTicksSinceEpoch) =>
        Assert.Equal(Epoch + expectedTicksSinceEpoch, CaptureTimestamps.ToTicksUtc(units, resolution, 0));

    [Fact]
    public void TimestampUnits_OffsetSecondsApplied() =>
        Assert.Equal(Epoch + 5 * Sec + 10, CaptureTimestamps.ToTicksUtc(1, 6, 5));

    [Fact]
    public void TimestampUnits_OutOfRange_Clamped()
    {
        Assert.Equal(DateTime.MaxValue.Ticks, CaptureTimestamps.ToTicksUtc(ulong.MaxValue, 7, 0));
        Assert.Equal(DateTime.MinValue.Ticks, CaptureTimestamps.ToTicksUtc(0, 6, long.MinValue / 2));
    }

    [Theory]
    [InlineData(39)]
    [InlineData(0x80 | 127)]
    public void TimestampResolution_Unsupported(byte resolution) => Assert.False(CaptureTimestamps.IsSupported(resolution));

    [Fact]
    public void PcapngReader_UnsupportedTsresol_IsCorrupt()
    {
        var body = new byte[8 + 8 + 4];
        BinaryPrimitives.WriteUInt16LittleEndian(body, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(8), 9);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(10), 1);
        body[12] = 60;
        byte[] bytes = [.. MinimalShb(), .. Block(1, body)];
        var c = ReadAll(bytes);
        Assert.Contains("if_tsresol", c.Error!.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ writers: golden bytes

    private static readonly long GoldenTicks = Epoch + Base * Sec + 1_234_567;
    private const string GoldenComment = "AcsTool session=00000000-0000-0000-0000-000000000001 exch=1 leg=CpeToProxy";

    [Fact]
    public void PcapngWriter_MatchesGoldenBytes()
    {
        using var ms = new MemoryStream();
        using (var w = new PcapngWriter(ms, new PcapngWriterOptions(UserApplication: "AcsTool test", Os: "test-os"), leaveOpen: true))
        {
            Assert.Equal(0, w.AddInterface(new PcapngInterface(LinkTypes.Ethernet, 65535, "eth0", "LAN", "tcp port 7547")));
            Assert.Equal(1, w.AddInterface(new PcapngInterface(LinkTypes.Null)));
            w.WritePacket(0, GoldenTicks, 5, Payload(5, 20), GoldenComment);
            w.WritePacket(1, GoldenTicks + 1, 100, Payload(8, 21));
            Assert.Equal(2, w.FramesWritten);
        }
        Assert.Equal(Fixture("golden-writer.pcapng"), ms.ToArray());
    }

    [Fact]
    public void PcapWriter_MatchesGoldenBytes()
    {
        using var ms = new MemoryStream();
        using (var w = new PcapWriter(ms, LinkTypes.Ethernet, 65535, leaveOpen: true))
        {
            w.WritePacket(GoldenTicks, 5, Payload(5, 20));
            w.WritePacket(GoldenTicks + 1, 100, Payload(8, 21));
        }
        Assert.Equal(Fixture("golden-writer.pcap"), ms.ToArray());
    }

    [Fact]
    public void PcapngWriter_DefaultSectionOptions_NameAcsToolAndOs()
    {
        using var ms = new MemoryStream();
        using (var w = new PcapngWriter(ms, leaveOpen: true))
        {
            w.AddInterface(new PcapngInterface(LinkTypes.Ethernet));
        }
        ms.Position = 0;
        var c = CaptureFileReader.ReadAll(ms);
        var s = Assert.Single(c.Sections);
        Assert.StartsWith("AcsTool ", s.UserApplication, StringComparison.Ordinal);
        Assert.DoesNotContain("+", s.UserApplication, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(s.Os));
        Assert.Equal(7, Assert.Single(c.Interfaces).TimestampResolution);
        Assert.Empty(c.Frames);
    }

    // ------------------------------------------------------------------ writers: round trip

    [Fact]
    public void PcapngWriter_RoundTrip_PreservesEverything()
    {
        var rnd = new Random(1234);
        var ifaces = new[]
        {
            new PcapngInterface(LinkTypes.Ethernet, 65535, "\\Device\\NPF_{ABC}", "Intel(R) Ethernet", "tcp port 7547 or tcp port 443"),
            new PcapngInterface(LinkTypes.Null, 262144, "\\Device\\NPF_Loopback", "Adapter for loopback traffic capture"),
            new PcapngInterface(LinkTypes.LinuxSll2, 0, "any"),
            new PcapngInterface(LinkTypes.FromLinkKind(LinkKind.Raw)),
        };
        var frames = new List<(int If, long Ticks, int Orig, byte[] Data, string? Comment)>();
        var ticks = Epoch + Base * Sec;
        for (var i = 0; i < 500; i++)
        {
            ticks += rnd.Next(1, 50_000);
            var data = new byte[rnd.Next(0, 1601)];
            rnd.NextBytes(data);
            var orig = data.Length + (i % 7 == 0 ? rnd.Next(1, 3000) : 0);
            var comment = (i % 5) switch
            {
                0 => $"AcsTool session={Guid.NewGuid()} exch={i} leg=CpeToProxy",
                1 => "ünïcödé ✓ " + new string('x', i % 13),
                _ => null,
            };
            frames.Add((i % ifaces.Length, ticks, orig, data, comment));
        }

        using var ms = new MemoryStream();
        using (var w = new PcapngWriter(ms, new PcapngWriterOptions(UserApplication: "AcsTool rt", Os: "os", Hardware: "hw"), leaveOpen: true))
        {
            foreach (var iface in ifaces)
            {
                w.AddInterface(iface);
            }
            foreach (var f in frames)
            {
                w.WritePacket(f.If, f.Ticks, f.Orig, f.Data, f.Comment);
            }
        }
        Assert.Equal(0, ms.Length % 4);
        ms.Position = 0;
        var c = CaptureFileReader.ReadAll(ms);

        Assert.Null(c.Error);
        Assert.Equal("hw", c.Sections[0].Hardware);
        Assert.Equal(ifaces.Length, c.Interfaces.Count);
        for (var i = 0; i < ifaces.Length; i++)
        {
            Assert.Equal(ifaces[i].LinkType, c.Interfaces[i].LinkType);
            Assert.Equal(ifaces[i].SnapLength, c.Interfaces[i].SnapLength);
            Assert.Equal(ifaces[i].Name, c.Interfaces[i].Name);
            Assert.Equal(ifaces[i].Description, c.Interfaces[i].Description);
            Assert.Equal(ifaces[i].Filter, c.Interfaces[i].Filter);
            Assert.Equal(7, c.Interfaces[i].TimestampResolution);
        }
        Assert.Equal([LinkKind.Ethernet, LinkKind.Null, LinkKind.LinuxSll2, LinkKind.Raw], c.Interfaces.Select(i => i.Link));
        Assert.Equal(frames.Count, c.Frames.Count);
        for (var i = 0; i < frames.Count; i++)
        {
            var (iface, t, orig, data, comment) = frames[i];
            var r = c.Frames[i];
            Assert.Equal(iface, r.InterfaceId);
            Assert.Equal(t, r.TimestampTicksUtc);
            Assert.Equal(orig, r.OriginalLength);
            Assert.Equal(data, r.Data);
            Assert.Equal(comment, r.Comment);
        }
    }

    [Fact]
    public void PcapngWriter_RawFrameOverload()
    {
        using var ms = new MemoryStream();
        using (var w = new PcapngWriter(ms, leaveOpen: true))
        {
            w.AddInterface(new PcapngInterface(LinkTypes.Ethernet));
            w.WritePacket(new RawFrame(0, GoldenTicks, 1500, new byte[] { 1, 2, 3 }), "c");
        }
        ms.Position = 0;
        var f = Assert.Single(CaptureFileReader.ReadAll(ms).Frames);
        Assert.Equal(GoldenTicks, f.TimestampTicksUtc);
        Assert.Equal(1500, f.OriginalLength);
        Assert.Equal(new byte[] { 1, 2, 3 }, f.Data);
        Assert.Equal("c", f.Comment);
    }

    [Theory]
    [InlineData(PcapTimestampPrecision.Nanosecond)]
    [InlineData(PcapTimestampPrecision.Microsecond)]
    public void PcapWriter_RoundTrip(PcapTimestampPrecision precision)
    {
        var rnd = new Random(99);
        var frames = Enumerable.Range(0, 200).Select(i =>
        {
            var d = new byte[rnd.Next(0, 1515)];
            rnd.NextBytes(d);
            return (Ticks: Epoch + Base * Sec + i * 1_234_567L + 3, Orig: d.Length + (i % 3), Data: d);
        }).ToList();

        using var ms = new MemoryStream();
        using (var w = new PcapWriter(ms, LinkTypes.Null, 262144, precision, leaveOpen: true))
        {
            Assert.Equal(LinkKind.Null, w.Link);
            foreach (var f in frames)
            {
                w.WritePacket(f.Ticks, f.Orig, f.Data);
            }
            w.WritePacket(new RawFrame(0, frames[0].Ticks, 4, new byte[] { 9, 9, 9, 9 }));
        }
        ms.Position = 0;
        var c = CaptureFileReader.ReadAll(ms);
        Assert.Null(c.Error);
        Assert.Equal(LinkKind.Null, c.Interfaces[0].Link);
        Assert.Equal(262144, c.Interfaces[0].SnapLength);
        Assert.Equal(frames.Count + 1, c.Frames.Count);
        for (var i = 0; i < frames.Count; i++)
        {
            var expected = precision == PcapTimestampPrecision.Nanosecond ? frames[i].Ticks : frames[i].Ticks / 10 * 10;
            Assert.Equal(expected, c.Frames[i].TimestampTicksUtc);
            Assert.Equal(frames[i].Orig, c.Frames[i].OriginalLength);
            Assert.Equal(frames[i].Data, c.Frames[i].Data);
        }
    }

    // ------------------------------------------------------------------ writers: argument checks

    [Fact]
    public void PcapngWriter_RejectsBadArguments()
    {
        using var ms = new MemoryStream();
        using var w = new PcapngWriter(ms);
        Assert.Throws<ArgumentOutOfRangeException>(() => w.WritePacket(0, GoldenTicks, 1, [1]));      // no interface yet
        w.AddInterface(new PcapngInterface(LinkTypes.Ethernet));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.WritePacket(1, GoldenTicks, 1, [1]));      // unknown interface
        Assert.Throws<ArgumentOutOfRangeException>(() => w.WritePacket(0, GoldenTicks, 1, [1, 2]));   // orig < captured
        Assert.Throws<ArgumentOutOfRangeException>(() => w.WritePacket(0, Epoch - 1, 1, [1]));       // before 1970
        Assert.Throws<ArgumentException>(() => w.WritePacket(0, GoldenTicks, 1, [1], new string('x', 70_000)));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.AddInterface(new PcapngInterface(70_000)));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.AddInterface(new PcapngInterface(1, -1)));
        Assert.Equal(0, w.FramesWritten);
    }

    [Fact]
    public void PcapWriter_RejectsBadArguments()
    {
        using var ms = new MemoryStream();
        using var w = new PcapWriter(ms, LinkTypes.Ethernet);
        Assert.Throws<ArgumentOutOfRangeException>(() => w.WritePacket(GoldenTicks, 1, [1, 2]));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.WritePacket(Epoch - 1, 1, [1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.WritePacket(Epoch + (1L << 32) * Sec, 1, [1])); // past 2106
    }

    [Fact]
    public void Writers_AfterDispose_Throw_And_LeaveOpenRespected()
    {
        var ms = new MemoryStream();
        var w = new PcapngWriter(ms, leaveOpen: true);
        w.AddInterface(new PcapngInterface(LinkTypes.Ethernet));
        w.Dispose();
        Assert.Throws<ObjectDisposedException>(() => w.WritePacket(0, GoldenTicks, 1, [1]));
        Assert.True(ms.CanWrite);

        var p = new PcapWriter(ms, LinkTypes.Ethernet);
        p.Dispose();
        Assert.Throws<ObjectDisposedException>(() => p.WritePacket(GoldenTicks, 1, [1]));
        Assert.False(ms.CanWrite);
    }

    [Fact]
    public void Reader_Dispose_ClosesStreamUnlessLeaveOpen()
    {
        var a = new MemoryStream(Fixture("us-le.pcap"));
        CaptureFileReader.Open(a).Dispose();
        Assert.False(a.CanRead);

        var b = new MemoryStream(Fixture("multi-section.pcapng"));
        CaptureFileReader.Open(b, leaveOpen: true).Dispose();
        Assert.True(b.CanRead);
        b.Dispose();
    }

    [Fact]
    public void LinkTypes_MapBothWays()
    {
        foreach (var kind in Enum.GetValues<LinkKind>().Where(k => k != LinkKind.Other))
        {
            Assert.Equal(kind, LinkTypes.ToLinkKind(LinkTypes.FromLinkKind(kind)));
        }
        Assert.Equal(LinkKind.Other, LinkTypes.ToLinkKind(147));
        Assert.Equal(LinkKind.Raw, LinkTypes.ToLinkKind(12)); // DLT_RAW on OpenBSD-ish platforms
        Assert.Throws<ArgumentOutOfRangeException>(() => LinkTypes.FromLinkKind(LinkKind.Other));
    }

    // ------------------------------------------------------------------ optional external validation

    private static string? FindOnPath(string tool)
    {
        var names = OperatingSystem.IsWindows() ? new[] { tool + ".exe" } : [tool];
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            foreach (var n in names)
            {
                var p = Path.Combine(dir, n);
                if (File.Exists(p))
                {
                    return p;
                }
            }
        }
        return null;
    }

    [SkippableFact]
    public void Capinfos_AcceptsWriterOutput_WhenOnPath()
    {
        var capinfos = FindOnPath("capinfos");
        Skip.If(capinfos is null, "capinfos is not on PATH (optional Wireshark validation).");

        var path = Path.Combine(Path.GetTempPath(), $"acstool-pt12-{Guid.NewGuid():N}.pcapng");
        try
        {
            using (var fs = File.Create(path))
            using (var w = new PcapngWriter(fs))
            {
                w.AddInterface(new PcapngInterface(LinkTypes.Ethernet, 65535, "eth0"));
                for (var i = 0; i < 3; i++)
                {
                    w.WritePacket(0, GoldenTicks + i, 60, Payload(60, i), "c" + i);
                }
            }
            var psi = new ProcessStartInfo(capinfos!, ["-c", path]) { RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);
            Assert.Equal(0, p.ExitCode);
            Assert.Matches(@"Number of packets:\s+3", output);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
