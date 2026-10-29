using AcsTool.Core.Sessions;
using AcsTool.Presentation.Abstractions;
using Microsoft.Extensions.Logging;

namespace AcsTool.Presentation.Feed;

public sealed record FeedChange(Guid Id, SessionChangeKind Kind, SessionSnapshot? Snapshot);

/// <summary>A coalesced batch of store changes. <see cref="Resync"/> carries the full store state after dropped notifications.</summary>
public sealed record FeedBatch(IReadOnlyList<FeedChange> Changes, bool Cleared, IReadOnlyList<SessionSnapshot>? Resync);

public interface IFeedBatchConsumer
{
    void Apply(FeedBatch batch);
}

/// <summary>
/// The single reader of <see cref="ISessionStore.Changes"/> (plan §3.8 rule 5): coalesces ids for 100 ms after the first
/// item (or up to 500 distinct ids), takes snapshots off the UI thread, then makes one UI dispatch per batch.
/// </summary>
public sealed class SessionFeedPump : IAsyncDisposable
{
    public static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(100);
    public const int MaxBatchIds = 500;

    private readonly ISessionStore _store;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ILogger<SessionFeedPump> _log;
    private readonly List<IFeedBatchConsumer> _consumers = [];
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _dispatches;
    private volatile bool _collecting;
    private readonly IBannerService? _banners;
    private int _forceResync;
    private int _errors;

    public const string ErrorBannerKey = "feed-error";

    public SessionFeedPump(ISessionStore store, IUiDispatcher dispatcher, TimeProvider time, ILogger<SessionFeedPump> log, IBannerService? banners = null)
    {
        _store = store;
        _dispatcher = dispatcher;
        _time = time;
        _log = log;
        _banners = banners;
    }

    /// <summary>Number of batch/consumer failures survived (review m6).</summary>
    public int ErrorCount => Volatile.Read(ref _errors);

    public int DispatchCount => Volatile.Read(ref _dispatches);

    /// <summary>True while a batch is being collected (the flush timer is armed). For deterministic tests.</summary>
    public bool IsCollecting => _collecting;

    public void Subscribe(IFeedBatchConsumer consumer)
    {
        lock (_consumers)
        {
            _consumers.Add(consumer);
        }
    }

    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (_loop is null)
        {
            return;
        }
        await _cts!.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _cts.Dispose();
        _cts = null;
        _loop = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var reader = _store.Changes;
        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await CollectAndFlushAsync(reader, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
#pragma warning disable CA1031 // one failed batch must not stop the feed for the rest of the process (review m6)
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    _collecting = false;
                    ReportFailure(ex);
                    await Task.Delay(FlushInterval, _time, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private void ReportFailure(Exception ex)
    {
        Interlocked.Increment(ref _errors);
        Interlocked.Exchange(ref _forceResync, 1); // the next batch carries the full store state
        _log.LogError(3501, ex, "Session feed batch failed; resyncing");
        if (_banners is { } banners)
        {
            _ = _dispatcher.InvokeAsync(() => banners.Show(BannerKind.Warning,
                "The session list hit an internal error and was resynchronized: " + ex.Message, ErrorBannerKey));
        }
    }

    private async Task CollectAndFlushAsync(System.Threading.Channels.ChannelReader<SessionChange> reader, CancellationToken ct)
    {
        var pending = new Dictionary<Guid, SessionChangeKind>();
        var cleared = false;
        using var flushCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var deadline = Task.Delay(FlushInterval, _time, flushCts.Token);
        _collecting = true;
        try
        {
            Drain(reader, pending, ref cleared);
            while (pending.Count < MaxBatchIds && !deadline.IsCompleted)
            {
                var more = reader.WaitToReadAsync(flushCts.Token).AsTask();
                var done = await Task.WhenAny(more, deadline).ConfigureAwait(false);
                if (done == deadline || !await more.ConfigureAwait(false))
                {
                    break;
                }
                Drain(reader, pending, ref cleared);
            }
            // Changes written before the deadline fired belong in this flush. FakeTimeProvider.Advance (and a busy thread pool
            // in general) can complete the deadline before the WaitToReadAsync continuation reaches Task.WhenAny, so the loop
            // above may exit with items still in the channel; drain once more (FLAKY-01).
            Drain(reader, pending, ref cleared);
            // Shutdown requested: skip the last UI dispatch while the app closes (review n2). Cancellation can end the loop via
            // the deadline or via WaitToReadAsync, so check explicitly instead of relying on which one won.
            ct.ThrowIfCancellationRequested();
            await flushCts.CancelAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The changes already read are dropped; a restarted pump starts with a full resync so nothing stays stale.
            Interlocked.Exchange(ref _forceResync, 1);
            throw;
        }
        finally
        {
            _collecting = false;
        }
        await FlushAsync(pending, cleared).ConfigureAwait(false);
    }

    private static void Drain(System.Threading.Channels.ChannelReader<SessionChange> reader, Dictionary<Guid, SessionChangeKind> pending, ref bool cleared)
    {
        while (pending.Count < MaxBatchIds && reader.TryRead(out var change))
        {
            if (change.Kind == SessionChangeKind.Cleared)
            {
                cleared = true;
                pending.Clear();
                continue;
            }
            pending[change.SessionId] = pending.TryGetValue(change.SessionId, out var prev) ? Max(prev, change.Kind) : change.Kind;
        }
    }

    /// <summary>Precedence Cleared &gt; Removed &gt; Ended &gt; Added &gt; Updated.</summary>
    internal static SessionChangeKind Max(SessionChangeKind a, SessionChangeKind b) => Rank(a) >= Rank(b) ? a : b;

    private static int Rank(SessionChangeKind k) => k switch
    {
        SessionChangeKind.Cleared => 4,
        SessionChangeKind.Removed => 3,
        SessionChangeKind.Ended => 2,
        SessionChangeKind.Added => 1,
        _ => 0,
    };

    private async Task FlushAsync(Dictionary<Guid, SessionChangeKind> pending, bool cleared)
    {
        var forced = Interlocked.Exchange(ref _forceResync, 0) == 1;
        IReadOnlyList<SessionSnapshot>? resync = _store.TryConsumeResync() | forced ? _store.GetAll() : null;
        var changes = pending.Select(p => new FeedChange(p.Key, p.Value, p.Value == SessionChangeKind.Removed ? null : _store.Get(p.Key))).ToList();
        var batch = new FeedBatch(changes, cleared, resync);
        IFeedBatchConsumer[] consumers;
        lock (_consumers)
        {
            consumers = [.. _consumers];
        }
        await _dispatcher.InvokeAsync(() =>
        {
            foreach (var c in consumers)
            {
                // Consumers are isolated: one throwing (e.g. a list view) does not starve the others.
                try
                {
                    c.Apply(batch);
                }
#pragma warning disable CA1031
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    ReportFailure(ex);
                }
            }
        }).ConfigureAwait(false);
        Interlocked.Increment(ref _dispatches);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
