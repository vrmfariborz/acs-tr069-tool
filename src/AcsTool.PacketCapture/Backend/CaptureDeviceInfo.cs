using System.Net;

namespace AcsTool.PacketCapture.Backend;

/// <summary>One live-capture device (design §5.2). <see cref="Name"/> is the pcap device name used by <see cref="ICaptureBackend.Open"/>.</summary>
public sealed record CaptureDeviceInfo(
    string Name,
    string FriendlyName,
    string? Description,
    IReadOnlyList<IPAddress> Addresses,
    bool IsLoopback,
    bool IsUp,
    string? MacAddress);
