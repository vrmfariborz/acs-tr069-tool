using AcsTool.Presentation.Abstractions;
using AcsTool.Presentation.Feed;
using AcsTool.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AcsTool.Presentation.Tests;

/// <summary>
/// TesterBot, FLAKY-01 (FeedPumpTests.TenThousandChangesOverOneSimulatedSecond): a change written just before the flush
/// deadline fires can miss that flush. FakeTimeProvider.Advance completes the deadline inline, while the channel's
/// WaitToReadAsync completion reaches Task.WhenAny only via a thread-pool continuation; when the pool is busy the deadline
/// wins and CollectAndFlushAsync breaks out without a final Drain.
/// </summary>
public class TesterFeedPumpRaceTests
{
    [Fact(Skip = "FLAKY-01: SessionFeedPump.CollectAndFlushAsync does not drain the channel once more after the deadline; a change written before Advance can miss that flush (fix: final Drain before FlushAsync)")]
    public async Task ChangeWrittenBeforeDeadline_IsInThatFlush_EvenWhenThePoolIsBusy()
    {
        var time = new FakeTimeProvider();
        var store = Kit.Store(time);
        var feed = Kit.Feed(store);
        await using var pump = new SessionFeedPump(store, new SynchronousUiDispatcher(), time, NullLogger<SessionFeedPump>.Instance);
        pump.Subscribe(feed);
        pump.Start();

        Kit.StartSession(store, "EARLY");
        Assert.True(await TestUtil.WaitUntilAsync(() => pump.IsCollecting && store.Changes.Count == 0), "pump never drained");
        await Task.Delay(50); // let the pump park in Task.WhenAny(more, deadline)

        // Occupy the thread-pool workers for a moment (what heavy CPU contention does by accident).
        using var gate = new ManualResetEventSlim(false);
        var blockers = Enumerable.Range(0, Environment.ProcessorCount * 4)
            .Select(_ => Task.Factory.StartNew(() => gate.Wait(TimeSpan.FromSeconds(5)), CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default))
            .ToArray();
        Thread.Sleep(100);
        Kit.StartSession(store, "LATE");          // written before the deadline...
        time.Advance(SessionFeedPump.FlushInterval); // ...which fires inline here
        gate.Set();
        await Task.WhenAll(blockers);

        Assert.True(await TestUtil.WaitUntilAsync(() => pump.DispatchCount >= 1), "no dispatch");
        var firstDispatch = string.Join(",", feed.Items.Select(i => i.Device).OrderBy(d => d, StringComparer.Ordinal));
        Assert.True(feed.Items.Count == 2, $"after dispatch {pump.DispatchCount} the feed has [{firstDispatch}]; LATE missed the flush");
    }
}
