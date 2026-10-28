using AcsTool.Core.Model;

namespace AcsTool.Core.Parsing;

public sealed record ParseResult(CwmpEnvelope? Envelope, CwmpMessageKind Kind,
    IReadOnlyList<string> Warnings, IReadOnlyList<string> Errors)
{
    public bool IsEmpty => Kind == CwmpMessageKind.Empty;

    /// <summary>Line of the XML error for <see cref="CwmpMessageKind.NotXml"/>.</summary>
    public int? ErrorLine { get; init; }

    public CwmpMessage? Message => Envelope?.Message;

    public static ParseResult EmptyBody { get; } = new(null, CwmpMessageKind.Empty, [], []);
}

public interface ICwmpParser
{
    /// <summary>Parses a CWMP HTTP body. Never throws.</summary>
    ParseResult Parse(ReadOnlySpan<byte> body, string? contentType = null);
}
