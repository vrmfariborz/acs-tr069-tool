using System.Diagnostics;

namespace AcsTool.Testing;

public static class TestUtil
{
    /// <summary>Polls until the condition holds (real time); returns false on timeout.</summary>
    public static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return true;
            }
            await Task.Delay(10);
        }
        return condition();
    }

    /// <summary>
    /// Runs a call that is expected to end through a client timeout. <paramref name="hangGuard"/> turns a missing timeout into
    /// a <see cref="TimeoutException"/> instead of a hung test; the elapsed time lets the caller assert the timeout's bounds.
    /// </summary>
    public static async Task<(T Result, TimeSpan Elapsed)> TimedAsync<T>(Func<Task<T>> call, TimeSpan hangGuard)
    {
        ArgumentNullException.ThrowIfNull(call);
        var sw = Stopwatch.StartNew();
        var result = await call().WaitAsync(hangGuard).ConfigureAwait(false);
        return (result, sw.Elapsed);
    }
}
