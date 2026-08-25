using System.Diagnostics;

namespace Srui.Testing;

/// <summary>Waiting on real time with nothing under test to iterate:
/// a service on its own thread, a watcher, a walk. A harness has
/// <see cref="Harness.Until"/>, which is this plus an iteration per
/// poll; this is for the tests that have no harness.</summary>
public static class Poll
{
    /// <summary>Poll until <paramref name="condition"/> holds, running
    /// <paramref name="each"/> before every check, or fail after
    /// <paramref name="timeoutMs"/> naming <paramref name="what"/>.</summary>
    public static void Until(
        Func<bool> condition, string what, int timeoutMs = 5000, Action? each = null)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            each?.Invoke();
            if (condition())
                return;
            if (clock.ElapsedMilliseconds >= timeoutMs)
                throw new SruiAssertException(
                    $"timed out after {timeoutMs} ms waiting for {what}");
            Thread.Sleep(5);
        }
    }
}
