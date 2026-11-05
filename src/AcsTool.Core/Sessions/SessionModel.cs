using System.Collections.Immutable;
using System.Net;
using AcsTool.Core.Cwmp;
using AcsTool.Core.Model;
using AcsTool.Core.Net;
using AcsTool.Core.Parsing;

namespace AcsTool.Core.Sessions;

public enum SessionSource { Proxy, Simulator }

public enum SessionOutcome
{
    InProgress, Completed, AuthFailed, UpstreamError, AcsFault, CpeFault,
    HttpError, Abandoned, Superseded, Canceled, ProtocolError,
}

/// <summary>Immutable capture of one HTTP message. The body may be a truncated copy.</summary>
public sealed class HttpMessageCapture
{
    public string? Method { get; init; }
    public string? Url { get; init; }
    public int? StatusCode { get; init; }
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } = [];
    public ReadOnlyMemory<byte> Body { get; init; }
    public long OriginalLength { get; init; }
    public bool Truncated { get; init; }

    /// <summary>Copies at most <paramref name="maxBytes"/> of the body.</summary>
    public static HttpMessageCapture From(string? method, string? url, int? statusCode,
        IReadOnlyList<KeyValuePair<string, string>> headers, ReadOnlySpan<byte> body, int maxBytes)
    {
        var take = Math.Min(body.Length, Math.Max(0, maxBytes));
        return new HttpMessageCapture
        {
            Method = method,
            Url = url,
            StatusCode = statusCode,
            Headers = headers,
            Body = body[..take].ToArray(),
            OriginalLength = body.Length,
            Truncated = take < body.Length,
        };
    }
}

public sealed record ExchangeTimings(DateTimeOffset Started, TimeSpan? Dns = null, TimeSpan? Connect = null,
    TimeSpan? ToResponseHeaders = null, TimeSpan? Total = null);

public sealed record ExchangeRecord(int Seq, HttpMessageCapture Request, MessageSummary RequestSummary,
    HttpMessageCapture? Response, MessageSummary? ResponseSummary, UpstreamError? Error,
    ExchangeTimings Timings, IReadOnlyList<string> Notes)
{
    public bool IsPending => Response is null && Error is null;

    /// <summary>Transport endpoints of this exchange (PT0.3, design §6.2); null until the producer reports them.</summary>
    public ExchangeEndpoints? Endpoints { get; init; }
}

/// <summary>
/// The TCP endpoints an exchange travelled over, used to correlate captured packets with exchanges (design §6.2, §6.3).
/// Downstream is CPE → proxy (proxy only); upstream is proxy/simulator → ACS. IPv4-mapped IPv6 addresses are normalised to IPv4.
/// </summary>
/// <param name="DownstreamConnectionId">Kestrel connection id (proxy only).</param>
/// <param name="DownstreamRemote">CPE ip:port as seen by the proxy.</param>
/// <param name="DownstreamLocal">Proxy ip:port the CPE connected to.</param>
/// <param name="UpstreamLocal">Ephemeral local ip:port of the socket to the ACS.</param>
/// <param name="UpstreamRemote">ACS ip:port actually connected (or the HTTP proxy's, when one is used).</param>
/// <param name="UpstreamConnectionSerial">Per-session upstream connection-attempt number: unique and increasing per session; pooled
/// reuse keeps it, each new connect attempt increments it, so failed attempts leave gaps.</param>
public sealed record ExchangeEndpoints(
    string? DownstreamConnectionId,
    IPEndPoint? DownstreamRemote,
    IPEndPoint? DownstreamLocal,
    IPEndPoint? UpstreamLocal,
    IPEndPoint? UpstreamRemote,
    long? UpstreamConnectionSerial)
{
    /// <summary>
    /// Builds endpoints from the downstream side and an upstream snapshot. Upstream fields are filled only when the snapshot says
    /// the socket carried this exchange (review m2); otherwise they are null.
    /// </summary>
    public static ExchangeEndpoints Create(string? downstreamConnectionId, IPEndPoint? downstreamRemote, IPEndPoint? downstreamLocal,
        UpstreamSnapshot? upstream)
    {
        var up = upstream is { CarriedCurrentExchange: true } u ? u : (UpstreamSnapshot?)null;
        return new ExchangeEndpoints(downstreamConnectionId, downstreamRemote, downstreamLocal, up?.Local, up?.Remote, up?.Serial)
            .Normalized();
    }

    /// <summary>Returns a copy with every endpoint normalised (IPv4-mapped IPv6 → IPv4).</summary>
    public ExchangeEndpoints Normalized() => this with
    {
        DownstreamRemote = EndPointUtil.Normalize(DownstreamRemote),
        DownstreamLocal = EndPointUtil.Normalize(DownstreamLocal),
        UpstreamLocal = EndPointUtil.Normalize(UpstreamLocal),
        UpstreamRemote = EndPointUtil.Normalize(UpstreamRemote),
    };
}

public sealed record SessionStart(SessionSource Source, string ClientEndpoint, string TargetUrl,
    DeviceIdStruct? DeviceId, IReadOnlyList<EventStruct> Events, CwmpVersion Version, string? Label);

public sealed record SessionSnapshot(Guid Id, SessionSource Source, string ClientEndpoint, string TargetUrl,
    DeviceIdStruct? DeviceId, IReadOnlyList<EventStruct> Events, CwmpVersion Version, DateTimeOffset Started,
    DateTimeOffset? Ended, SessionOutcome Outcome, UpstreamError? LastError, TlsInfo? Tls,
    ImmutableList<ExchangeRecord> Exchanges, long CapturedBytes, IReadOnlyList<string> Notes)
{
    public string? Label { get; init; }

    public bool IsActive => Ended is null;

    public TimeSpan? Duration => Ended is { } e ? e - Started : null;
}

public enum SessionChangeKind { Added, Updated, Ended, Removed, Cleared }

public readonly record struct SessionChange(Guid SessionId, SessionChangeKind Kind);

public sealed record CaptureStats(int Sessions, int Active, long CapturedBytes, long Evicted, long DroppedNotifications);

/// <summary>Producer side of the store. Thread-safe and non-blocking.</summary>
public interface ISessionSink
{
    Guid StartSession(SessionStart start);

    int AddExchange(Guid sessionId, HttpMessageCapture request, MessageSummary summary, DateTimeOffset started);

    void CompleteExchange(Guid sessionId, int seq, HttpMessageCapture? response, MessageSummary? summary,
        UpstreamError? error, ExchangeTimings timings, IReadOnlyList<string>? notes = null);

    void UpdateSession(Guid sessionId, Func<SessionSnapshot, SessionSnapshot> patch);

    void EndSession(Guid sessionId, SessionOutcome outcome);

    /// <summary>
    /// Attaches transport endpoints to an exchange (PT0.3). The default is a no-op so existing sinks and test fakes keep compiling;
    /// unknown sessions or sequence numbers are ignored.
    /// </summary>
    void SetExchangeEndpoints(Guid sessionId, int seq, ExchangeEndpoints endpoints)
    {
    }
}

/// <summary>Consumer side of the store.</summary>
public interface ISessionStore : ISessionSink
{
    System.Threading.Channels.ChannelReader<SessionChange> Changes { get; }

    SessionSnapshot? Get(Guid id);

    /// <summary>Ordered by Started.</summary>
    IReadOnlyList<SessionSnapshot> GetAll();

    void Clear();

    /// <summary>Removes all sessions matching the predicate (raises Removed).</summary>
    int RemoveWhere(Func<SessionSnapshot, bool> predicate);

    CaptureStats Stats { get; }

    /// <summary>True once after notifications were dropped; consumers should then do a full resync.</summary>
    bool TryConsumeResync();
}
