using System.Collections.Immutable;
using System.Threading.Channels;
using AcsTool.Core.Net;
using AcsTool.Core.Parsing;
using AcsTool.Core.Settings;

namespace AcsTool.Core.Sessions;

/// <summary>
/// The single source of truth for captured sessions (plan §3.5.4, §3.8 rules 4 and 7). One lock per operation;
/// snapshots are immutable and replaced under the lock. Changes are announced on a bounded DropOldest channel.
/// </summary>
public sealed class InMemorySessionStore : ISessionStore
{
    public const int ChannelCapacity = 10_000;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, LinkedListNode<SessionSnapshot>> _byId = [];
    private readonly LinkedList<SessionSnapshot> _ordered = new();
    private readonly Channel<SessionChange> _channel;
    private CaptureSettings _settings;
    private readonly TimeProvider _time;
    private long _capturedBytes;
    private int _active;
    private long _evicted;
    private long _dropped;
    private int _resync;

    public InMemorySessionStore(CaptureSettings settings, TimeProvider time)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _channel = Channel.CreateBounded<SessionChange>(
            new BoundedChannelOptions(ChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            },
            _ =>
            {
                Interlocked.Increment(ref _dropped);
                Interlocked.Exchange(ref _resync, 1);
            });
    }

    public ChannelReader<SessionChange> Changes => _channel.Reader;

    public CaptureStats Stats
    {
        get
        {
            lock (_gate)
            {
                return new CaptureStats(_byId.Count, _active, _capturedBytes, _evicted, Interlocked.Read(ref _dropped));
            }
        }
    }

    /// <summary>Current eviction limits (MaxSessions, CaptureBudgetBytes, ...).</summary>
    public CaptureSettings Limits
    {
        get
        {
            lock (_gate)
            {
                return _settings;
            }
        }
    }

    /// <summary>Applies new capture limits at runtime (Settings window / settings load) and evicts down to them immediately.</summary>
    public void UpdateLimits(CaptureSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        List<Guid> evicted;
        lock (_gate)
        {
            _settings = settings;
            evicted = EvictLocked();
        }
        NotifyRemoved(evicted);
    }

    /// <summary>
    /// Budget estimate for one captured message (review M4): body bytes plus UTF-16 size of headers, summary fields and warnings.
    /// Fixed-size parts (titles, timings) are ignored. Summaries are capped (MessageSummarizer.MaxListedItems), so this stays small.
    /// </summary>
    internal static long EstimateBytes(HttpMessageCapture? capture, MessageSummary? summary)
    {
        long n = 0;
        if (capture is not null)
        {
            n += capture.Body.Length;
            foreach (var h in capture.Headers)
            {
                n += 2L * (h.Key.Length + (h.Value?.Length ?? 0));
            }
        }
        if (summary is not null)
        {
            foreach (var f in summary.Fields)
            {
                n += 2L * (f.Key.Length + (f.Value?.Length ?? 0));
            }
            foreach (var w in summary.Warnings)
            {
                n += 2L * w.Length;
            }
        }
        return n;
    }

    public bool TryConsumeResync() => Interlocked.Exchange(ref _resync, 0) == 1;

    public Guid StartSession(SessionStart start)
    {
        ArgumentNullException.ThrowIfNull(start);
        var id = Guid.NewGuid();
        var snap = new SessionSnapshot(id, start.Source, start.ClientEndpoint, start.TargetUrl, start.DeviceId,
            start.Events, start.Version, _time.GetUtcNow(), null, SessionOutcome.InProgress, null, null,
            ImmutableList<ExchangeRecord>.Empty, 0, [])
        { Label = start.Label };
        List<Guid> evicted;
        lock (_gate)
        {
            _byId[id] = _ordered.AddLast(snap);
            _active++;
            evicted = EvictLocked();
        }
        Notify(id, SessionChangeKind.Added);
        NotifyRemoved(evicted);
        return id;
    }

    public int AddExchange(Guid sessionId, HttpMessageCapture request, MessageSummary summary, DateTimeOffset started)
    {
        ArgumentNullException.ThrowIfNull(request);
        int seq = -1;
        List<Guid>? evicted = null;
        lock (_gate)
        {
            if (!_byId.TryGetValue(sessionId, out var node))
            {
                return -1;
            }
            var s = node.Value;
            seq = s.Exchanges.Count + 1;
            var ex = new ExchangeRecord(seq, request, summary, null, null, null, new ExchangeTimings(started), []);
            var bytes = EstimateBytes(request, summary);
            node.Value = s with { Exchanges = s.Exchanges.Add(ex), CapturedBytes = s.CapturedBytes + bytes };
            _capturedBytes += bytes;
            evicted = EvictLocked();
        }
        Notify(sessionId, SessionChangeKind.Updated);
        NotifyRemoved(evicted);
        return seq;
    }

    public void CompleteExchange(Guid sessionId, int seq, HttpMessageCapture? response, MessageSummary? summary,
        UpstreamError? error, ExchangeTimings timings, IReadOnlyList<string>? notes = null)
    {
        List<Guid>? evicted;
        lock (_gate)
        {
            if (!_byId.TryGetValue(sessionId, out var node))
            {
                return;
            }
            var s = node.Value;
            var idx = seq - 1;
            if (idx < 0 || idx >= s.Exchanges.Count)
            {
                return;
            }
            var old = s.Exchanges[idx];
            var bytes = EstimateBytes(response, summary);
            var updated = old with
            {
                Response = response,
                ResponseSummary = summary,
                Error = error,
                Timings = timings,
                Notes = notes is null || notes.Count == 0 ? old.Notes : [.. old.Notes, .. notes],
            };
            node.Value = s with
            {
                Exchanges = s.Exchanges.SetItem(idx, updated),
                CapturedBytes = s.CapturedBytes + bytes,
                LastError = error ?? s.LastError,
            };
            _capturedBytes += bytes;
            evicted = EvictLocked();
        }
        Notify(sessionId, SessionChangeKind.Updated);
        NotifyRemoved(evicted);
    }

    public void UpdateSession(Guid sessionId, Func<SessionSnapshot, SessionSnapshot> patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        lock (_gate)
        {
            if (!_byId.TryGetValue(sessionId, out var node))
            {
                return;
            }
            var s = node.Value;
            var p = patch(s);
            // identity, timing, exchanges and outcome are owned by the store
            node.Value = p with
            {
                Id = s.Id,
                Exchanges = s.Exchanges,
                CapturedBytes = s.CapturedBytes,
                Outcome = s.Outcome,
                Ended = s.Ended,
                Started = s.Started,
            };
        }
        Notify(sessionId, SessionChangeKind.Updated);
    }

    public void EndSession(Guid sessionId, SessionOutcome outcome)
    {
        List<Guid>? evicted;
        lock (_gate)
        {
            if (!_byId.TryGetValue(sessionId, out var node) || node.Value.Ended is not null)
            {
                return;
            }
            node.Value = node.Value with { Ended = _time.GetUtcNow(), Outcome = outcome };
            _active--;
            evicted = EvictLocked();
        }
        Notify(sessionId, SessionChangeKind.Ended);
        NotifyRemoved(evicted);
    }

    public SessionSnapshot? Get(Guid id)
    {
        lock (_gate)
        {
            return _byId.TryGetValue(id, out var node) ? node.Value : null;
        }
    }

    public IReadOnlyList<SessionSnapshot> GetAll()
    {
        lock (_gate)
        {
            return [.. _ordered];
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _byId.Clear();
            _ordered.Clear();
            _capturedBytes = 0;
            _active = 0;
        }
        Notify(Guid.Empty, SessionChangeKind.Cleared);
    }

    public int RemoveWhere(Func<SessionSnapshot, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var removed = new List<Guid>();
        lock (_gate)
        {
            var node = _ordered.First;
            while (node is not null)
            {
                var next = node.Next;
                if (predicate(node.Value))
                {
                    RemoveLocked(node);
                    removed.Add(node.Value.Id);
                }
                node = next;
            }
        }
        NotifyRemoved(removed);
        return removed.Count;
    }

    private void RemoveLocked(LinkedListNode<SessionSnapshot> node)
    {
        _ordered.Remove(node);
        _byId.Remove(node.Value.Id);
        _capturedBytes -= node.Value.CapturedBytes;
        if (node.Value.Ended is null)
        {
            _active--;
        }
    }

    /// <summary>Evicts the oldest ended sessions while over MaxSessions or the capture budget. Never evicts active sessions.</summary>
    private List<Guid> EvictLocked()
    {
        List<Guid>? evicted = null;
        if (_byId.Count <= _settings.MaxSessions && _capturedBytes <= _settings.CaptureBudgetBytes)
        {
            return [];
        }
        var node = _ordered.First;
        while (node is not null && (_byId.Count > _settings.MaxSessions || _capturedBytes > _settings.CaptureBudgetBytes))
        {
            var next = node.Next;
            if (node.Value.Ended is not null)
            {
                RemoveLocked(node);
                _evicted++;
                (evicted ??= []).Add(node.Value.Id);
            }
            node = next;
        }
        return evicted ?? [];
    }

    private void Notify(Guid id, SessionChangeKind kind) => _channel.Writer.TryWrite(new SessionChange(id, kind));

    private void NotifyRemoved(List<Guid>? ids)
    {
        if (ids is null)
        {
            return;
        }
        foreach (var id in ids)
        {
            Notify(id, SessionChangeKind.Removed);
        }
    }
}
