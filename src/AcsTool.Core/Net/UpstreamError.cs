namespace AcsTool.Core.Net;

public enum UpstreamErrorKind { None, Dns, Connect, Tls, Timeout, HttpStatus, Protocol, Canceled, BodyTooLarge, Unknown }

public enum RequestPhase { Dns, Connect, Tls, AwaitingResponse, ReadingBody }

public sealed record UpstreamError(UpstreamErrorKind Kind, RequestPhase Phase, string Message,
    string? ExceptionType = null, string? SocketError = null, int? HttpStatus = null, string? TlsDetail = null)
{
    /// <summary>Short display text, e.g. "CONNECT ConnectionRefused", "TIMEOUT AwaitingResponse", "HTTP 500".</summary>
    public string ShortText => Kind switch
    {
        UpstreamErrorKind.HttpStatus => $"HTTP {HttpStatus}",
        UpstreamErrorKind.Timeout => $"TIMEOUT {Phase}",
        UpstreamErrorKind.Connect => SocketError is null ? "CONNECT" : $"CONNECT {SocketError}",
        UpstreamErrorKind.Dns => "DNS",
        UpstreamErrorKind.Tls => TlsDetail is null ? "TLS" : $"TLS {TlsDetail}",
        _ => Kind.ToString().ToUpperInvariant(),
    };

    public override string ToString() => $"{ShortText}: {Message}";
}
