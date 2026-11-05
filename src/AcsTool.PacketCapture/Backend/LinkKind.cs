using PacketDotNet;

namespace AcsTool.PacketCapture.Backend;

/// <summary>Link-layer kind for a capture session / frame (design §6.1). Maps PacketDotNet / pcap DLT values.</summary>
public enum LinkKind
{
    Ethernet,
    Null,
    Loop,
    Raw,
    LinuxSll,
    LinuxSll2,
    Other,
}

public static class LinkKindMapping
{
    public static LinkKind FromPacketDotNet(LinkLayers layers) => layers switch
    {
        LinkLayers.Ethernet => LinkKind.Ethernet,
        LinkLayers.Null => LinkKind.Null,
        LinkLayers.Loop => LinkKind.Loop,
        LinkLayers.Raw => LinkKind.Raw,
        LinkLayers.LinuxSll => LinkKind.LinuxSll,
        LinkLayers.LinuxSll2 => LinkKind.LinuxSll2,
        _ => LinkKind.Other,
    };
}
