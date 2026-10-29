using AcsTool.Core.Sessions;
using AcsTool.Core.Settings;
using AcsTool.Presentation.Abstractions;
using AcsTool.Presentation.Feed;
using AcsTool.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AcsTool.Presentation.Tests;

public class FeedPumpTests
{
    [Fact]
    public async Task TenThousandChangesOverOneSimulatedSecond_AtMostTenDispatches_ItemsUpdatedInPlace()
    {
        var time = new FakeTimeProvider();
        var store = Kit.Store(time);
        var feed = Kit.Feed(store);
        var dispatcher = new SynchronousUiDispatcher();
        await using var pump = new SessionFeedPump(store, dispatcher, time, NullLogger<SessionFeedPump>.Instance);
        pump.Subscribe(feed);
        pump.Start();

        var ids = Enumerable.Range(0, 50).Select(i => Kit.StartSession(store, "S" + i)).ToList();
        var changes = ids.Count;
        Dictionary<Guid, SessionItemViewModel>? firstInstances = null;
        for (var round = 0; round < 10; round++)
        {
            while (changes < (round + 1) * 1000)
            {
                var id = ids[changes % ids.Count];
                store.UpdateSession(id, s => s with { Notes = [$"n{changes}"] });
                changes++;
            }
            Assert.True(await TestUtil.WaitUntilAsync(() => pump.IsCollecting), "pump never started collecting");
            time.Advance(SessionFeedPump.FlushInterval);
            var expected = round + 1;
            Assert.True(await TestUtil.WaitUntilAsync(() => pump.DispatchCount == expected), $"dispatch {expected} missing");
            firstInstances ??= feed.Items.ToDictionary(i => i.Id);
        }
        Assert.Equal(10_000, changes);
        Assert.True(pump.DispatchCount <= 10, $"dispatches: {pump.DispatchCount}");
        Assert.Equal(50, feed.Items.Count);
        Assert.All(feed.Items, i => Assert.Same(firstInstances![i.Id], i));
        // the last update is visible: notes are not in the item, but the snapshot is the latest one
        Assert.All(feed.Items, i => Assert.Equal(store.Get(i.Id)!.Notes, i.Snapshot.Notes));
    }

    [Fact]
    public async Task DroppedNotifications_ResyncReproducesStoreState()
    {
        var store = Kit.Store(capture: new CaptureSettings(MaxSessions: 20_000));
        // 12,500 notifications before the pump runs: the bounded channel (10k, DropOldest) drops 2,500 → resync requested
        for (var i = 0; i < 5000; i++)
        {
            var id = Kit.StartSession(store, "R" + i);
            store.UpdateSession(id, s => s with { Notes = ["x"] });
            if (i % 2 == 0)
            {
                store.EndSession(id, SessionOutcome.Completed);
            }
        }
        Assert.True(store.Stats.DroppedNotifications > 0);
        var feed = Kit.Feed(store, max: 10_000);
        await using var pump = new SessionFeedPump(store, new SynchronousUiDispatcher(), TimeProvider.System, NullLogger<SessionFeedPump>.Instance);
        pump.Subscribe(feed);
        pump.Start();
        Assert.True(await TestUtil.WaitUntilAsync(() => feed.TotalCount == 5000 && pump.DispatchCount > 0), $"feed has {feed.TotalCount}");
        var byId = feed.Items.ToDictionary(i => i.Id);
        foreach (var s in store.GetAll())
        {
            Assert.Equal(s.Outcome, byId[s.Id].Outcome);
        }
    }

    [Theory]
    [InlineData(SessionChangeKind.Updated, SessionChangeKind.Added, SessionChangeKind.Added)]
    [InlineData(SessionChangeKind.Ended, SessionChangeKind.Updated, SessionChangeKind.Ended)]
    [InlineData(SessionChangeKind.Removed, SessionChangeKind.Ended, SessionChangeKind.Removed)]
    [InlineData(SessionChangeKind.Cleared, SessionChangeKind.Removed, SessionChangeKind.Cleared)]
    public void ChangePrecedence(SessionChangeKind a, SessionChangeKind b, SessionChangeKind expected)
    {
        Assert.Equal(expected, SessionFeedPump.Max(a, b));
        Assert.Equal(expected, SessionFeedPump.Max(b, a));
    }
}

public class FeedViewModelTests
{
    private readonly InMemorySessionStore _store = Kit.Store();

    [Fact]
    public void NewestOnTop_AndSourceFilter()
    {
        var feed = Kit.Feed(_store);
        var a = Kit.StartSession(_store, "A");
        var b = Kit.StartSession(_store, "B");
        var sim = Kit.StartSession(_store, "S", SessionSource.Simulator);
        feed.Apply(Kit.Batch(_store, (a, SessionChangeKind.Added), (b, SessionChangeKind.Added), (sim, SessionChangeKind.Added)));
        Assert.Equal(new[] { "00D09E-B", "00D09E-A" }, feed.Items.Select(i => i.Device));
        var oldest = Kit.Feed(_store, newestOnTop: false);
        oldest.Apply(Kit.Batch(_store, (a, SessionChangeKind.Added), (b, SessionChangeKind.Added)));
        Assert.Equal(new[] { "00D09E-A", "00D09E-B" }, oldest.Items.Select(i => i.Device));
    }

    [Fact]
    public void TrimsToMaxFeedItems_FromOldEnd()
    {
        var feed = Kit.Feed(_store, max: 20);
        var ids = Enumerable.Range(0, 50).Select(i => Kit.StartSession(_store, "T" + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture))).ToList();
        feed.Apply(Kit.Batch(_store, ids.Select(id => (id, SessionChangeKind.Added)).ToArray()));
        Assert.Equal(20, feed.Items.Count);
        Assert.Equal("00D09E-T49", feed.Items[0].Device);
        Assert.Equal("00D09E-T30", feed.Items[^1].Device);
    }

    [Fact]
    public void Filters_TextAndOutcome()
    {
        var feed = Kit.Feed(_store);
        var ok = Kit.StartSession(_store, "OK1");
        var bad = Kit.StartSession(_store, "BAD1");
        _store.EndSession(ok, SessionOutcome.Completed);
        _store.EndSession(bad, SessionOutcome.UpstreamError);
        feed.Apply(Kit.Batch(_store, (ok, SessionChangeKind.Added), (bad, SessionChangeKind.Added)));
        feed.OutcomeFilter = OutcomeFilter.ErrorsOnly;
        Assert.Equal("00D09E-BAD1", Assert.Single(feed.Items).Device);
        feed.OutcomeFilter = OutcomeFilter.All;
        feed.FilterText = "ok1";
        Assert.Equal("00D09E-OK1", Assert.Single(feed.Items).Device);
        feed.FilterText = "";
        Assert.Equal(2, feed.Items.Count);
    }

    [Fact]
    public void Pause_AccumulatesAndAppliesOnResume()
    {
        var feed = Kit.Feed(_store);
        feed.IsPaused = true;
        var a = Kit.StartSession(_store, "P");
        feed.Apply(Kit.Batch(_store, (a, SessionChangeKind.Added)));
        Assert.Empty(feed.Items);
        feed.TogglePauseCommand.Execute(null);
        Assert.Single(feed.Items);
    }

    [Fact]
    public void Pause_CoalescesById_EvictedSessionsDoNotReappear()
    {
        var store = Kit.Store(capture: new CaptureSettings(MaxSessions: 5));
        var feed = Kit.Feed(store);
        feed.IsPaused = true;
        for (var i = 0; i < 20; i++)
        {
            var id = Kit.StartSession(store, $"E{i}");
            feed.Apply(Kit.Batch(store, (id, SessionChangeKind.Added)));
            for (var u = 0; u < 50; u++)
            {
                feed.Apply(Kit.Batch(store, (id, SessionChangeKind.Updated)));
            }
            store.EndSession(id, SessionOutcome.Completed);
            feed.Apply(Kit.Batch(store, (id, SessionChangeKind.Ended)));
        }
        Assert.Equal(20, feed.PausedPendingCount); // one entry per session, not one per batch (1,040 batches)
        Assert.Empty(feed.Items);
        feed.IsPaused = false;
        Assert.Equal(0, feed.PausedPendingCount);
        Assert.Equal(5, feed.Items.Count); // the 15 sessions evicted during the pause are not resurrected
        Assert.All(feed.Items, i => Assert.NotNull(store.Get(i.Id)));
    }

    [Fact]
    public void Pause_ManyDistinctSessions_FallsBackToResync_Bounded()
    {
        var store = Kit.Store(capture: new CaptureSettings(MaxSessions: 100));
        var feed = Kit.Feed(store);
        feed.IsPaused = true;
        for (var i = 0; i < SessionFeedViewModel.PausedPendingLimit + 10; i++)
        {
            var id = Guid.NewGuid();
            feed.Apply(new FeedBatch([new FeedChange(id, SessionChangeKind.Added, null)], false, null));
        }
        Assert.True(feed.PausedPendingCount <= SessionFeedViewModel.PausedPendingLimit);
        var live = Kit.StartSession(store, "LIVE");
        feed.IsPaused = false;
        Assert.Equal(live, Assert.Single(feed.Items).Id);
    }

    [Fact]
    public void SelectedSessionUpdated_DetailRefreshed_RemovedClearsSelection()
    {
        var feed = Kit.Feed(_store);
        var a = Kit.StartSession(_store, "D");
        feed.Apply(Kit.Batch(_store, (a, SessionChangeKind.Added)));
        feed.SelectedItem = feed.Items[0];
        Assert.Equal("00D09E-D", feed.Detail.DeviceKey);
        _store.EndSession(a, SessionOutcome.Completed);
        feed.Apply(Kit.Batch(_store, (a, SessionChangeKind.Ended)));
        Assert.Equal(SessionOutcome.Completed, feed.Detail.Session!.Outcome);
        feed.Apply(Kit.Batch(_store, (a, SessionChangeKind.Removed)));
        Assert.Null(feed.SelectedItem);
        Assert.Empty(feed.Items);
    }

    [Fact]
    public void Clear_RemovesOnlyThisSourceFromStore()
    {
        var feed = Kit.Feed(_store);
        var p = Kit.StartSession(_store, "P1");
        Kit.StartSession(_store, "S1", SessionSource.Simulator);
        feed.Apply(Kit.Batch(_store, (p, SessionChangeKind.Added)));
        feed.ClearCommand.Execute(null);
        Assert.Empty(feed.Items);
        Assert.Equal(SessionSource.Simulator, Assert.Single(_store.GetAll()).Source);
    }
}
