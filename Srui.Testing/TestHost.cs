namespace Srui.Testing;

/// <summary>A headless multi-app host with a recording reader on its
/// shared stream — the harness for behavioral tests of several apps
/// under one host, and the base a shell's own harness derives from.
/// Input goes to the host (switching combos first, then the active
/// app), speech is what the shared reader hears under the host's
/// background policy, and <see cref="Tick"/> is one host iteration:
/// messages delivered, apps ticked, a hosted app's Quit folded into
/// its Close, the host's <see cref="MultiAppHost.Ticked"/> run. Time
/// is the host's clock, which <see cref="Harness.Wait"/> advances.</summary>
public class TestHost : Harness
{
    /// <summary>The headless host under test.</summary>
    public MultiAppHost Host { get; } = MultiAppHost.Headless();

    /// <summary>An empty headless host: add apps, activate one, drive it.</summary>
    public TestHost() => Host.AddReader(Reader);

    /// <inheritdoc/>
    public override void Dispose() => Host.Dispose();

    /// <inheritdoc/>
    protected override SpeechVerbosity Verbosity => Host.Verbosity;

    /// <inheritdoc/>
    protected override void DispatchEvents() => Host.DispatchEvents();

    /// <inheritdoc/>
    protected override bool HandleInput(in InputEvent input) => Host.HandleInput(input);

    /// <inheritdoc/>
    protected override bool HandleKey(in KeyInput key) => Host.HandleKey(key);

    /// <inheritdoc/>
    protected override void Advance(ulong milliseconds) =>
        Host.TickAt(Host.Now + milliseconds);

    /// <summary>One host iteration at the same instant — a step. What
    /// the iteration speaks (an app closing on its own Quit, the
    /// shell's per-iteration work) is the next batch.</summary>
    public void Tick() => Wait(0);
}
