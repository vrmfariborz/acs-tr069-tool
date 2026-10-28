using System.Net;
using AcsTool.Core.Cwmp;
using AcsTool.Core.Model;
using AcsTool.Core.Net;
using AcsTool.Core.Parsing;
using AcsTool.Core.Sessions;
using AcsTool.Core.Settings;
using Microsoft.Extensions.Logging;

namespace AcsTool.Proxy;

/// <summary>
/// Groups proxied requests into CWMP sessions (plan §3.6.3) and decides when they end (§3.6.4).
/// All indexes live under one lock; nothing awaits while holding it.
/// </summary>
public sealed class CpeSessionTracker : IAsyncDisposable
{
    public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(5);

    private readonly ProxySettings _settings;
    private readonly IUpstreamHandlerFactory _handlers;
    private readonly ISessionSink _sink;
    private readonly TimeProvider _time;
    private readonly ILogger<CpeSessionTracker> _log;
    private readonly object _lock = new();
    private readonly Dictionary<string, ProxySession> _byConnection = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProxySession> _byCookie = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ProxySession>> _byIp = new(StringComparer.Ordinal);
    private readonly List<ProxySession> _active = [];
    private ITimer? _sweeper;

    public CpeSessionTracker(ProxySettings settings, IUpstreamHandlerFactory handlers, ISessionSink sink, TimeProvider time, ILogger<CpeSessionTracker> log)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public int ActiveCount
    {
        get { lock (_lock) { return _active.Count; } }
    }

    public TimeSpan IdleTimeout => TimeSpan.FromSeconds(Math.Max(1, _settings.SessionIdleTimeoutSeconds));

    public void StartSweeper()
    {
        lock (_lock)
        {
            _sweeper ??= _time.CreateTimer(_ => Sweep(), null, SweepInterval, SweepInterval);
        }
    }

    public void StopSweeper()
    {
        lock (_lock)
        {
            _sweeper?.Dispose();
            _sweeper = null;
        }
    }

    public ProxySession Resolve(IncomingRequest req, ParseResult parsed, MessageSummary summary)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(summary);
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            ProxySession? session;
            if (parsed.Kind == CwmpMessageKind.Inform)
            {
                var key = summary.DeviceId?.Key;
                session = FindAuthRetry(req, key);
                if (session is null)
                {
                    if (key is not null && _byIp.TryGetValue(req.RemoteIp, out var sameIp))
                    {
                        foreach (var old in sameIp.Where(s => s.DeviceKey == key).ToList())
                        {
                            EndLocked(old, SessionOutcome.Superseded);
                        }
                    }
                    session = CreateLocked(req, summary.DeviceId, summary.Events ?? [], summary.Version, label: null, orphan: false, oneShot: false);
                }
            }
            else
            {
                session = FindExisting(req, allowIpFallback: req.Method == "POST");
                if (session is null)
                {
                    var nonCwmp = req.Method != "POST";
                    session = CreateLocked(req, null, [], summary.Version,
                        nonCwmp ? "Non-CWMP request" : $"Unknown session from {req.RemoteIp}", orphan: true, oneShot: nonCwmp);
                }
            }
            IndexConnectionLocked(session, req.ConnectionId);
            session.LastActivity = now;
            return session;
        }
    }

    /// <summary>Creates a one-shot orphan session (e.g. for a request rejected before parsing).</summary>
    public ProxySession CreateOrphan(IncomingRequest req, string label)
    {
        ArgumentNullException.ThrowIfNull(req);
        lock (_lock)
        {
            return CreateLocked(req, null, [], CwmpVersion.Unknown, label, orphan: true, oneShot: true);
        }
    }

    private ProxySession? FindAuthRetry(IncomingRequest req, string? key)
    {
        if (_byConnection.TryGetValue(req.ConnectionId, out var byConn) && byConn.DeviceKey == key && byConn.LastStatus == 401)
        {
            return byConn;
        }
        if (_byIp.TryGetValue(req.RemoteIp, out var list))
        {
            return list.LastOrDefault(s => s.DeviceKey == key && s.LastStatus == 401);
        }
        return null;
    }

    private ProxySession? FindExisting(IncomingRequest req, bool allowIpFallback)
    {
        if (_byConnection.TryGetValue(req.ConnectionId, out var byConn))
        {
            return byConn;
        }
        foreach (var cookieHeader in HeaderUtil.GetAll(req.Headers, "Cookie"))
        {
            foreach (var pair in cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (_byCookie.TryGetValue(pair, out var byCookie))
                {
                    return byCookie;
                }
            }
        }
        if (allowIpFallback && _byIp.TryGetValue(req.RemoteIp, out var list) && list.Count > 0)
        {
            var candidates = list.Where(s => !s.IsOneShot).ToList();
            if (candidates.Count == 0)
            {
                return null;
            }
            var chosen = candidates.MaxBy(s => s.LastActivity)!;
            if (candidates.Count > 1)
            {
                _sink.UpdateSession(chosen.SessionId, s => s with { Notes = [.. s.Notes, "ambiguous correlation by IP"] });
            }
            return chosen;
        }
        return null;
    }

    private ProxySession CreateLocked(IncomingRequest req, DeviceIdStruct? deviceId, IReadOnlyList<EventStruct> events,
        CwmpVersion version, string? label, bool orphan, bool oneShot)
    {
        var id = _sink.StartSession(new SessionStart(SessionSource.Proxy, req.ClientEndpoint, _settings.UpstreamUrl, deviceId, events, version, label));
        var tracker = new PhaseTracker();
        var managed = _settings.CookieMode == CookieMode.ManagedPerCpe;
        var cookies = managed ? new CookieContainer() : null;
        ProxySession? session = null;
        var handler = _handlers.Create(
            new UpstreamHandlerOptions(TimeSpan.FromSeconds(Math.Max(1, _settings.ConnectTimeoutSeconds)), _settings.Tls ?? TlsOptions.Default,
                UseCookies: managed, cookies, Credentials: null, AllowAutoRedirect: false, _settings.UseSystemProxy),
            () => tracker,
            tls =>
            {
                if (session is not null)
                {
                    session.LastTlsPolicyErrors = tls.PolicyErrors;
                }
                _sink.UpdateSession(id, s => s with { Tls = tls });
            });
        session = new ProxySession(id, req.RemoteIp, deviceId?.Key, tracker, handler, cookies, _time.GetUtcNow(), orphan, oneShot);
        _active.Add(session);
        if (!_byIp.TryGetValue(req.RemoteIp, out var list))
        {
            _byIp[req.RemoteIp] = list = [];
        }
        list.Add(session);
        _log.LogDebug(1001, "Session {Session} started for {Client} ({Device})", id, req.ClientEndpoint, deviceId?.Key ?? "?");
        return session;
    }

    private void IndexConnectionLocked(ProxySession session, string connectionId)
    {
        if (_byConnection.TryGetValue(connectionId, out var previous) && previous != session)
        {
            previous.ConnectionIds.Remove(connectionId);
        }
        _byConnection[connectionId] = session;
        session.ConnectionIds.Add(connectionId);
    }

    /// <summary>Indexes Set-Cookie name=value pairs of the upstream response.</summary>
    public void ObserveResponse(ProxySession session, ForwardResult result, CwmpMessageKind requestKind, bool requestWasEmpty)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(result);
        lock (_lock)
        {
            if (!session.IsEnded)
            {
                foreach (var sc in HeaderUtil.GetAll(result.Headers, "Set-Cookie"))
                {
                    var pair = sc.Split(';', 2)[0].Trim();
                    if (pair.Length > 0)
                    {
                        _byCookie[pair] = session;
                        session.CookiePairs.Add(pair);
                    }
                }
            }
            session.LastStatus = result.StatusCode;
            session.LastRequestKind = requestKind;
            session.LastRequestWasEmpty = requestWasEmpty;
            session.LastActivity = _time.GetUtcNow();
        }
    }

    /// <summary>Session end rules (§3.6.4), evaluated after each exchange before ObserveResponse.</summary>
    public static SessionOutcome? EvaluateEnd(ProxySession session, CwmpMessageKind requestKind, ForwardResult result,
        CwmpMessageKind? responseKind, IList<string> notes)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(notes);
        if (result.IsFailure)
        {
            return SessionOutcome.UpstreamError;
        }
        var status = result.StatusCode!.Value;
        if (requestKind == CwmpMessageKind.Fault)
        {
            session.SawCpeFault = true;
        }
        if (requestKind == CwmpMessageKind.Empty)
        {
            session.CpeSentEmpty = true;
        }
        else if (!requestKind.IsResponse() && requestKind != CwmpMessageKind.Fault)
        {
            session.CpeSentEmpty = false; // a new CPE-originated request (Inform, TransferComplete, ...)
        }
        if (responseKind == CwmpMessageKind.Fault)
        {
            session.SawAcsFault = true;
        }
        if (status == 401)
        {
            notes.Add("AuthChallenge");
            return session.LastStatus == 401 && session.LastRequestKind == requestKind ? SessionOutcome.AuthFailed : null;
        }
        if (status == 403)
        {
            return SessionOutcome.AuthFailed;
        }
        if (status >= 400)
        {
            return SessionOutcome.HttpError;
        }
        var emptyResponse = status == 204 || (status is >= 200 and < 300 && result.Body.Length == 0);
        if (emptyResponse)
        {
            // TR-069 §3.7.1.4: the session ends when the ACS answers empty after the CPE signalled "no more requests"
            // (its empty POST), either on that empty POST itself or on a later CPE response/fault.
            if (requestKind == CwmpMessageKind.Empty || session.CpeSentEmpty)
            {
                return session.SawAcsFault ? SessionOutcome.AcsFault : session.SawCpeFault ? SessionOutcome.CpeFault : SessionOutcome.Completed;
            }
            if (requestKind == CwmpMessageKind.Inform)
            {
                notes.Add("ACS answered the Inform with an empty response (InformResponse expected)");
                return SessionOutcome.ProtocolError;
            }
            notes.Add("empty ACS response");
        }
        if (session.IsOneShot)
        {
            return SessionOutcome.Completed;
        }
        return null;
    }

    public ValueTask EndAsync(ProxySession session, SessionOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_lock)
        {
            EndLocked(session, outcome);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask EndAllAsync(SessionOutcome outcome)
    {
        lock (_lock)
        {
            foreach (var s in _active.ToList())
            {
                EndLocked(s, outcome);
            }
        }
        return ValueTask.CompletedTask;
    }

    private void EndLocked(ProxySession session, SessionOutcome outcome)
    {
        if (session.IsEnded)
        {
            return;
        }
        session.IsEnded = true;
        _active.Remove(session);
        foreach (var c in session.ConnectionIds)
        {
            if (_byConnection.TryGetValue(c, out var s) && s == session)
            {
                _byConnection.Remove(c);
            }
        }
        foreach (var p in session.CookiePairs)
        {
            if (_byCookie.TryGetValue(p, out var s) && s == session)
            {
                _byCookie.Remove(p);
            }
        }
        if (_byIp.TryGetValue(session.ClientIp, out var list))
        {
            list.Remove(session);
            if (list.Count == 0)
            {
                _byIp.Remove(session.ClientIp);
            }
        }
        _sink.EndSession(session.SessionId, outcome);
        session.Dispose();
        _log.LogDebug(1002, "Session {Session} ended: {Outcome}", session.SessionId, outcome);
    }

    internal void Sweep()
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            foreach (var s in _active.ToList())
            {
                if (now - s.LastActivity >= IdleTimeout && s.Gate.CurrentCount == 1)
                {
                    EndLocked(s, SessionOutcome.Abandoned);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        StopSweeper();
        await EndAllAsync(SessionOutcome.Canceled).ConfigureAwait(false);
    }
}
