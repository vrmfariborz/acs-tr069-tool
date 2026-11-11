namespace AcsTool.PacketCapture.Files;

/// <summary>Capture file container formats handled by the managed readers/writers (design §9, D5).</summary>
public enum CaptureFileFormat
{
    /// <summary>Classic libpcap format (µs or ns magic, either byte order).</summary>
    Pcap,

    /// <summary>PCAP Next Generation (draft-ietf-opsawg-pcapng).</summary>
    Pcapng,
}
