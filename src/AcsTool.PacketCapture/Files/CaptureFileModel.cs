using AcsTool.PacketCapture.Backend;

namespace AcsTool.PacketCapture.Files;

/// <summary>One section of a capture file: a PCAPNG Section Header Block, or the PCAP global header.</summary>
public sealed record CaptureFileSection(
    int Index,
    bool BigEndian,
    int MajorVersion,
    int MinorVersion,
    string? UserApplication = null,
    string? Os = null,
    string? Hardware = null,
    string? Comment = null);

/// <summary>
/// One interface of a capture file. <see cref="Id"/> is file-global (it keeps counting across PCAPNG sections) and is the
/// value in <see cref="CaptureFileFrame.InterfaceId"/>; <see cref="LocalId"/> is the index inside its section.
/// <see cref="TimestampResolution"/> is the raw <c>if_tsresol</c> byte (6 = µs default, 9 = ns, 7 = 100 ns; MSB set = 2^-n).
/// </summary>
public sealed record CaptureFileInterface(
    int Id,
    int SectionIndex,
    int LocalId,
    int LinkType,
    int SnapLength,
    byte TimestampResolution,
    long TimestampOffsetSeconds = 0,
    string? Name = null,
    string? Description = null,
    string? Filter = null)
{
    public LinkKind Link => LinkTypes.ToLinkKind(LinkType);
}

/// <summary>
/// One frame read from a file. <see cref="TimestampTicksUtc"/> uses the <see cref="DateTime.Ticks"/> epoch (like
/// <see cref="RawFrame"/>); it is the Unix epoch when <see cref="HasTimestamp"/> is false (PCAPNG Simple Packet Block).
/// <see cref="Comment"/> is the <c>opt_comment</c> value(s), joined with <c>\n</c> when a block has several.
/// </summary>
public sealed record CaptureFileFrame(
    int InterfaceId,
    long TimestampTicksUtc,
    int OriginalLength,
    byte[] Data,
    string? Comment = null,
    bool HasTimestamp = true);

/// <summary>
/// Read limits. Defaults follow D8 (<c>PacketCaptureSettings.MaxFrames</c> / <c>FrameBudgetBytes</c>); <see cref="MaxBlockBytes"/>
/// rejects absurd block or record lengths before allocating (corrupt input).
/// </summary>
public sealed record CaptureReadLimits(int MaxFrames = 200_000, long MaxBytes = 134_217_728, int MaxBlockBytes = 16 * 1024 * 1024)
{
    public static CaptureReadLimits Default { get; } = new();
}

/// <summary>
/// Result of <see cref="CaptureFileReader.ReadAll(Stream, CaptureReadLimits?, bool)"/>. A corrupt or truncated block after the
/// header does not throw: <see cref="Frames"/> holds everything before it and <see cref="Error"/> says where reading stopped.
/// <see cref="StoppedAtLimit"/> is true when the frame or byte budget cut the file short ("file truncated at frame N").
/// </summary>
public sealed record CaptureFileContent(
    CaptureFileFormat Format,
    IReadOnlyList<CaptureFileSection> Sections,
    IReadOnlyList<CaptureFileInterface> Interfaces,
    IReadOnlyList<CaptureFileFrame> Frames,
    bool StoppedAtLimit,
    CaptureFileException? Error);
