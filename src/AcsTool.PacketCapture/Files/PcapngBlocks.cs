namespace AcsTool.PacketCapture.Files;

/// <summary>PCAPNG block types and option codes used by the reader and writer (draft-ietf-opsawg-pcapng).</summary>
internal static class PcapngBlocks
{
    public const uint SectionHeader = 0x0A0D0D0A;
    public const uint InterfaceDescription = 0x00000001;
    public const uint ObsoletePacket = 0x00000002;
    public const uint SimplePacket = 0x00000003;
    public const uint EnhancedPacket = 0x00000006;
    public const uint ByteOrderMagic = 0x1A2B3C4D;

    public const ushort OptEndOfOpt = 0;
    public const ushort OptComment = 1;

    public const ushort ShbHardware = 2;
    public const ushort ShbOs = 3;
    public const ushort ShbUserAppl = 4;

    public const ushort IfName = 2;
    public const ushort IfDescription = 3;
    public const ushort IfTsresol = 9;
    public const ushort IfFilter = 11;
    public const ushort IfTsoffset = 14;

    /// <summary>Block type + total length, then the trailing total length.</summary>
    public const int BlockOverhead = 12;
}
