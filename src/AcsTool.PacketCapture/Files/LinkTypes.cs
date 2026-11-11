using AcsTool.PacketCapture.Backend;

namespace AcsTool.PacketCapture.Files;

/// <summary>
/// pcap/PCAPNG LINKTYPE_* values for the link kinds AcsTool decodes (design §6.1), and the mapping to <see cref="LinkKind"/>.
/// No PacketDotNet or SharpPcap dependency, so file I/O never loads native code.
/// </summary>
public static class LinkTypes
{
    public const int Null = 0;
    public const int Ethernet = 1;
    public const int Raw = 101;
    public const int Loop = 108;
    public const int LinuxSll = 113;
    public const int LinuxSll2 = 276;

    /// <summary>DLT_RAW as written by some platforms' libpcap (12, 14) instead of LINKTYPE_RAW (101).</summary>
    private const int DltRawAlt1 = 12;
    private const int DltRawAlt2 = 14;

    public static LinkKind ToLinkKind(int linkType) => linkType switch
    {
        Null => LinkKind.Null,
        Ethernet => LinkKind.Ethernet,
        Raw or DltRawAlt1 or DltRawAlt2 => LinkKind.Raw,
        Loop => LinkKind.Loop,
        LinuxSll => LinkKind.LinuxSll,
        LinuxSll2 => LinkKind.LinuxSll2,
        _ => LinkKind.Other,
    };

    /// <summary>The LINKTYPE_* value for <paramref name="kind"/>. <see cref="LinkKind.Other"/> has none; keep the original value instead.</summary>
    public static int FromLinkKind(LinkKind kind) => kind switch
    {
        LinkKind.Null => Null,
        LinkKind.Ethernet => Ethernet,
        LinkKind.Raw => Raw,
        LinkKind.Loop => Loop,
        LinkKind.LinuxSll => LinuxSll,
        LinkKind.LinuxSll2 => LinuxSll2,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "LinkKind.Other has no single link type; use the original LINKTYPE value."),
    };
}
