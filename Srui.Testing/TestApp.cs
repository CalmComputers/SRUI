namespace Srui.Testing;

/// <summary>A headless app with a recording reader — the harness for
/// behavioral tests of one app: build widgets, push input, assert what
/// the reader hears. The step and batch discipline, the input methods,
/// time, and scenarios are <see cref="Harness"/>'s.</summary>
public sealed class TestApp : Harness
{
    /// <summary>The headless app under test.</summary>
    public SruiApp App { get; } = SruiApp.Headless();

    /// <summary>An empty headless app: build widgets against
    /// <see cref="App"/>, then drive it.</summary>
    public TestApp() => App.AddReader(Reader);

    /// <summary>Build the UI and establish initial focus, leaving the
    /// focus announcement queued as the first speech batch — assert it,
    /// or let the first step discard it.</summary>
    public TestApp(Action<SruiApp> build) : this()
    {
        build(App);
        App.EnsureFocus();
    }

    /// <inheritdoc/>
    public override void Dispose() => App.Dispose();

    /// <inheritdoc/>
    protected override SpeechVerbosity Verbosity => App.SpeechVerbosity;

    /// <inheritdoc/>
    protected override void DispatchEvents() => App.DispatchEvents();

    /// <inheritdoc/>
    protected override bool HandleInput(in InputEvent input) => App.HandleInput(input);

    /// <inheritdoc/>
    protected override bool HandleKey(in KeyInput key) => App.HandleKey(key);

    /// <inheritdoc/>
    protected override void Advance(ulong milliseconds) =>
        App.TickAt(App.Now + milliseconds);
}
