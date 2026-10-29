using AcsTool.Core.Cwmp;
using AcsTool.Core.Parsing;
using AcsTool.Core.Sessions;
using AcsTool.Core.Settings;
using Microsoft.Extensions.Time.Testing;

namespace AcsTool.Core.Tests;

public class SessionStoreTests
{
    private static SessionStart Start(string ip = "10.0.0.1:1") =>
        new(SessionSource.Proxy, ip, "http://acs", null, [], CwmpVersion.V1_0, null);

    private static HttpMessageCapture Cap(int bytes) =>
        HttpMessageCapture.From("POST", "/acs", null, [], new byte[bytes], int.MaxValue);

    private static List<SessionChange> Drain(ISessionStore s)
    {
        var list = new List<SessionChange>();
        while (s.Changes.TryRead(out var c))
        {
            list.Add(c);
        }
        return list;
    }

    [Fact]
    public void Lifecycle_StartAddCompleteEnd_NotifiesEachMutation()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var store = new InMemorySessionStore(new CaptureSettings(), time);
        var id = store.StartSession(Start());
        var seq = store.AddExchange(id, Cap(10), MessageSummary.None, time.GetUtcNow());
        Assert.True(store.Get(id)!.Exchanges[0].IsPending);
        store.CompleteExchange(id, seq, Cap(5), MessageSummary.None, null, new ExchangeTimings(time.GetUtcNow(), Total: TimeSpan.FromMilliseconds(3)), ["n1"]);
        time.Advance(TimeSpan.FromSeconds(2));
        store.EndSession(id, SessionOutcome.Completed);

        var s = store.Get(id)!;
        Assert.Equal(1, seq);
        Assert.Equal(SessionOutcome.Completed, s.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(2), s.Duration);
        Assert.False(s.Exchanges[0].IsPending);
        Assert.Equal(["n1"], s.Exchanges[0].Notes);
        Assert.Equal(15, s.CapturedBytes);
        Assert.Equal(new[] { SessionChangeKind.Added, SessionChangeKind.Updated, SessionChangeKind.Updated, SessionChangeKind.Ended },
            Drain(store).Select(c => c.Kind));
        Assert.Equal(new CaptureStats(1, 0, 15, 0, 0), store.Stats);
    }

    [Fact]
    public void EndSession_Twice_IsIgnored()
    {
        var store = new InMemorySessionStore(new CaptureSettings(), TimeProvider.System);
        var id = store.StartSession(Start());
        store.EndSession(id, SessionOutcome.Completed);
        store.EndSession(id, SessionOutcome.Abandoned);
        Assert.Equal(SessionOutcome.Completed, store.Get(id)!.Outcome);
        Assert.Equal(0, store.Stats.Active);
    }

    [Fact]
    public void Eviction_MaxSessions_EvictsOldestEndedNeverActive()
    {
        var store = new InMemorySessionStore(new CaptureSettings(MaxSessions: 3), TimeProvider.System);
        var active = store.StartSession(Start());
        var ended = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var id = store.StartSession(Start());
            store.EndSession(id, SessionOutcome.Completed);
            ended.Add(id);
        }
        var all = store.GetAll().Select(s => s.Id).ToList();
        Assert.Equal(3, all.Count);
        Assert.Contains(active, all);
        Assert.Equal(ended.TakeLast(2), all.Skip(1));
        Assert.Equal(3, store.Stats.Evicted);
        Assert.Contains(Drain(store), c => c.Kind == SessionChangeKind.Removed);
    }

    [Fact]
    public void UpdateLimits_Lowered_EvictsImmediatelyAndAppliesToLaterSessions()
    {
        var store = new InMemorySessionStore(new CaptureSettings(), TimeProvider.System);
        for (var i = 0; i < 8; i++)
        {
            store.EndSession(store.StartSession(Start()), SessionOutcome.Completed);
        }
        Drain(store);
        store.UpdateLimits(new CaptureSettings(MaxSessions: 3));
        Assert.Equal(3, store.Stats.Sessions);
        Assert.Equal(5, Drain(store).Count(c => c.Kind == SessionChangeKind.Removed));
        store.EndSession(store.StartSession(Start()), SessionOutcome.Completed);
        Assert.Equal(3, store.Stats.Sessions);
        Assert.Equal(3, store.Limits.MaxSessions);
    }

    [Fact]
    public void Eviction_ByteBudget_KeepsActiveEvenIfOverBudget()
    {
        var store = new InMemorySessionStore(new CaptureSettings(CaptureBudgetBytes: 100), TimeProvider.System);
        var a = store.StartSession(Start());
        store.AddExchange(a, Cap(80), MessageSummary.None, DateTimeOffset.UnixEpoch);
        store.EndSession(a, SessionOutcome.Completed);
        var b = store.StartSession(Start());
        store.AddExchange(b, Cap(80), MessageSummary.None, DateTimeOffset.UnixEpoch);
        Assert.Null(store.Get(a));
        Assert.NotNull(store.Get(b));
        var c = store.StartSession(Start());
        store.AddExchange(c, Cap(80), MessageSummary.None, DateTimeOffset.UnixEpoch);
        Assert.NotNull(store.Get(b));
        Assert.NotNull(store.Get(c));
        Assert.Equal(160, store.Stats.CapturedBytes);
    }

    [Fact]
    public void Capture_From_TruncatesAndFlags()
    {
        var cap = HttpMessageCapture.From("POST", "/", null, [], new byte[100], 40);
        Assert.Equal(40, cap.Body.Length);
        Assert.Equal(100, cap.OriginalLength);
        Assert.True(cap.Truncated);
        Assert.False(HttpMessageCapture.From("POST", "/", null, [], new byte[10], 40).Truncated);
    }

    [Fact]
    public async Task Concurrency_8Producers_ConsistentFinalState()
    {
        var store = new InMemorySessionStore(new CaptureSettings(MaxSessions: 100_000), TimeProvider.System);
        const int producers = 8;
        const int opsPerProducer = 10_000;
        var ids = new System.Collections.Concurrent.ConcurrentBag<Guid>();
        var tasks = Enumerable.Range(0, producers).Select(p => Task.Run(() =>
        {
            var id = Guid.Empty;
            for (var i = 0; i < opsPerProducer; i++)
            {
                switch (i % 4)
                {
                    case 0:
                        id = store.StartSession(Start($"10.0.0.{p}:1"));
                        ids.Add(id);
                        break;
                    case 1:
                        store.AddExchange(id, Cap(1), MessageSummary.None, DateTimeOffset.UnixEpoch);
                        break;
                    case 2:
                        store.CompleteExchange(id, 1, Cap(1), MessageSummary.None, null, new ExchangeTimings(DateTimeOffset.UnixEpoch));
                        break;
                    default:
                        store.EndSession(id, SessionOutcome.Completed);
                        break;
                }
            }
        })).ToArray();
        await Task.WhenAll(tasks);

        var all = store.GetAll();
        Assert.Equal(producers * opsPerProducer / 4, all.Count);
        Assert.All(all, s =>
        {
            Assert.Equal(SessionOutcome.Completed, s.Outcome);
            Assert.Single(s.Exchanges);
            Assert.Equal(2, s.CapturedBytes);
        });
        Assert.Equal(0, store.Stats.Active);
        Assert.Equal(all.Count * 2L, store.Stats.CapturedBytes);
        // 80,000 notifications into a 10,000 channel: oldest dropped, counted, resync flagged
        Assert.Equal(producers * opsPerProducer - InMemorySessionStore.ChannelCapacity, store.Stats.DroppedNotifications);
        Assert.True(store.TryConsumeResync());
        Assert.False(store.TryConsumeResync());
    }

    [Fact]
    public void RemoveWhere_And_Clear_Notify()
    {
        var store = new InMemorySessionStore(new CaptureSettings(), TimeProvider.System);
        var a = store.StartSession(Start());
        store.StartSession(Start() with { Source = SessionSource.Simulator });
        Drain(store);
        Assert.Equal(1, store.RemoveWhere(s => s.Source == SessionSource.Proxy));
        Assert.Equal(new SessionChange(a, SessionChangeKind.Removed), Assert.Single(Drain(store)));
        store.Clear();
        Assert.Empty(store.GetAll());
        Assert.Equal(SessionChangeKind.Cleared, Assert.Single(Drain(store)).Kind);
        Assert.Equal(0, store.Stats.Active);
    }

    [Fact]
    public void UpdateSession_CannotOverrideStoreOwnedFields()
    {
        var store = new InMemorySessionStore(new CaptureSettings(), TimeProvider.System);
        var id = store.StartSession(Start());
        store.UpdateSession(id, s => s with { Notes = ["hello"], Outcome = SessionOutcome.Completed });
        var snap = store.Get(id)!;
        Assert.Equal(["hello"], snap.Notes);
        Assert.Equal(SessionOutcome.InProgress, snap.Outcome);
    }
}
