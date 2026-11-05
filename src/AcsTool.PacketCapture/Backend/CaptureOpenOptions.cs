namespace AcsTool.PacketCapture.Backend;

/// <summary>Options for <see cref="ICaptureBackend.Open"/> (design §5.2, defaults from §7).</summary>
public sealed record CaptureOpenOptions(
    int Snaplen = 65_535,
    bool Promiscuous = false,
    int KernelBufferBytes = 16_777_216,
    string? BpfFilter = null,
    int ReadTimeoutMilliseconds = 100,
    bool PreferNanosecondTimestamps = true,
    int InterfaceId = 0)
{
    public static CaptureOpenOptions Default { get; } = new();
}
