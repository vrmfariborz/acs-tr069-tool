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
}
