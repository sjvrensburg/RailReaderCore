namespace RailReader.Core.Tests;

/// <summary>
/// Waits for background work in a test whose controller runs on a <see cref="QueuedThreadMarshaller"/>.
/// Posted callbacks (prep submissions, render completions) only run when the test pumps, so a
/// wait loop must pump on every pass, or it stalls on work that is queued but never run (#174).
/// </summary>
internal static class TestWait
{
    /// <summary>
    /// Pumps <paramref name="marshaller"/>, runs <paramref name="tick"/>, and re-checks
    /// <paramref name="condition"/> until it holds or <paramref name="timeoutMs"/> pass. Returns
    /// the condition's final value, so a caller asserts on it with its own message.
    /// </summary>
    public static bool Until(Func<bool> condition, QueuedThreadMarshaller marshaller,
        Action? tick = null, int timeoutMs = 10000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            marshaller.Pump();
            tick?.Invoke();
            if (condition()) return true;
            if (sw.ElapsedMilliseconds >= timeoutMs) return false;
            Thread.Sleep(5);
        }
    }
}
