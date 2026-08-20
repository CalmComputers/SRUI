using Srui.Core;

namespace Srui.Testing;

/// <summary>A speech assertion failed. Carries a message readable as
/// plain text: what was expected, what was actually spoken.</summary>
public class SruiAssertException : Exception
{
    /// <summary>Create with the full failure message.</summary>
    public SruiAssertException(string message) : base(message) { }
}

/// <summary>A headless app with a recording reader — the harness for
/// behavioral tests: build widgets, push input, assert what the reader
/// hears. Input methods simulate a real host faithfully: <see cref="Press(KeyCombo)"/>
/// runs the same physical-to-logical mapping the SDL host runs, delivers
/// the physical key phases around the logical input in host order, and
/// synthesizes the TypeChar an unmodified printable key produces.
/// Combo strings accept both the config form ("ctrl+shift+t") and
/// compact modifier initials ("cs+t"); see <see cref="ComboSpec"/>.</summary>
public sealed class TestApp : IDisposable
{
    /// <summary>The headless app under test.</summary>
    public SruiApp App { get; } = SruiApp.Headless();

    /// <summary>The recording reader attached to <see cref="App"/>.</summary>
    public RecordingReader Reader { get; } = new();

    /// <summary>An empty headless app: build widgets against
    /// <see cref="App"/>, then drive it.</summary>
    public TestApp() => App.AddReader(Reader);

    /// <summary>Build the UI and establish initial focus, leaving the
    /// focus announcement queued as the first speech batch — assert it,
    /// or let the first input flush it.</summary>
    public TestApp(Action<SruiApp> build) : this()
    {
        build(App);
        App.EnsureFocus();
    }

    /// <inheritdoc/>
    public void Dispose() => App.Dispose();

    // ── Speech ──

    /// <summary>Deliver queued output and return the utterances heard
    /// since the last delivery, in order, rendered at full verbosity.</summary>
    public List<string> Spoken()
    {
        App.DispatchEvents();
        var result = Reader.Events
            .Select(SpeechRenderer.RenderEvent)
            .OfType<string>()
            .ToList();
        Reader.Events.Clear();
        return result;
    }

    /// <summary>Deliver queued output, discarding it.</summary>
    public void Drain()
    {
        App.DispatchEvents();
        Reader.Events.Clear();
    }

    /// <summary>Assert that exactly these utterances were spoken since
    /// the last delivery, in order. No arguments asserts silence
    /// (equivalent to <see cref="ExpectNoSpeech"/>).</summary>
    public void Expect(params string[] utterances)
    {
        var spoken = Spoken();
        if (spoken.SequenceEqual(utterances, StringComparer.Ordinal))
            return;
        throw new SruiAssertException(
            $"expected {Describe(utterances)} but heard {Describe(spoken)}");
    }

    /// <summary>Assert that nothing was spoken since the last delivery.</summary>
    public void ExpectNoSpeech()
    {
        var spoken = Spoken();
        if (spoken.Count != 0)
            throw new SruiAssertException(
                $"expected no speech but heard {Describe(spoken)}");
    }

    private static string Describe(IReadOnlyList<string> utterances) =>
        utterances.Count == 0
            ? "nothing"
            : string.Join(", ", utterances.Select(u => $"\"{u}\""));

    // ── Logical input ──

    /// <summary>Dispatch one logical input kind. True when consumed.</summary>
    public bool Input(InputKind kind) => App.HandleInput(InputEvent.Simple(kind));

    /// <summary>Dispatch one logical input event. True when consumed.</summary>
    public bool Input(InputEvent ev) => App.HandleInput(ev);

    /// <summary>Type one character. True when consumed.</summary>
    public bool Type(char c) => App.HandleInput(InputEvent.TypeChar(c));

    /// <summary>Type a string, one codepoint at a time (astral characters
    /// arrive whole, as they would from a real host).</summary>
    public void Type(string text)
    {
        foreach (var rune in text.EnumerateRunes())
            App.HandleInput(new InputEvent(InputKind.TypeChar, (uint)rune.Value, 0, Mods.None));
    }

    /// <summary>Dispatch a combo as a bare RawKey input, bypassing the
    /// host mapping — for driving widget shortcuts directly. True when
    /// consumed. <see cref="Press(KeyCombo)"/> is the full key-tap
    /// simulation; prefer it for user-fidelity tests.</summary>
    public bool Raw(KeyCombo combo)
    {
        var (key, mods) = combo.ToFlat();
        return App.HandleInput(InputEvent.RawKey(key, mods));
    }

    // ── Key-tap simulation ──

    /// <summary>Tap a key combo exactly as a real host would deliver it:
    /// the physical Press phase, then the mapped logical input (or the
    /// TypeChar an unmodified printable produces, uppercased and
    /// symbol-shifted under a US layout when Shift is held), then the
    /// physical Release phase. True when any stage consumed something.</summary>
    public bool Press(KeyCombo combo)
    {
        var (key, mods) = combo.ToFlat();
        var handled = App.HandleKey(new KeyInput(key, mods, KeyPhase.Press));
        handled |= HandleLogical(combo);
        handled |= App.HandleKey(new KeyInput(key, mods, KeyPhase.Release));
        return handled;
    }

    /// <summary>Tap a key combo given as a string — config form
    /// ("ctrl+shift+t") or compact initials ("cs+t").</summary>
    public bool Press(string combo) => Press(ComboSpec.Parse(combo));

    /// <summary>Hold a key down: the physical Press phase plus the
    /// logical input the keydown produces, matching a real host. The key
    /// stays held until <see cref="Up(KeyCombo)"/>. Auto-repeat is not
    /// simulated. True when any stage consumed something.</summary>
    public bool Down(KeyCombo combo)
    {
        var (key, mods) = combo.ToFlat();
        var handled = App.HandleKey(new KeyInput(key, mods, KeyPhase.Press));
        return HandleLogical(combo) | handled;
    }

    /// <summary>Hold a key down, combo given as a string.</summary>
    public bool Down(string combo) => Down(ComboSpec.Parse(combo));

    /// <summary>Release a held key: the physical Release phase.
    /// True when a handler consumed it.</summary>
    public bool Up(KeyCombo combo)
    {
        var (key, mods) = combo.ToFlat();
        return App.HandleKey(new KeyInput(key, mods, KeyPhase.Release));
    }

    /// <summary>Release a held key, combo given as a string.</summary>
    public bool Up(string combo) => Up(ComboSpec.Parse(combo));

    private bool HandleLogical(KeyCombo combo)
    {
        if (InputMapper.MapCombo(combo) is InputEvent mapped)
            return App.HandleInput(mapped);
        if (TypedChar(combo) is char c)
        {
            var (key, mods) = combo.ToFlat();
            return App.HandleInput(new InputEvent(InputKind.TypeChar, c, key, mods));
        }
        return false;
    }

    /// <summary>The character a keydown of this combo delivers as text
    /// input, if any: unmodified printables and Space, with Shift applying
    /// a US-layout uppercase/symbol shift. Null for everything else.</summary>
    private static char? TypedChar(KeyCombo combo)
    {
        if (combo.Ctrl || combo.Alt)
            return null;
        if (combo.Key == Key.Space)
            return ' ';
        if (!combo.Key.IsChar(out var c))
            return null;
        return combo.Shift ? ShiftedUs(c) : c;
    }

    private static char ShiftedUs(char c) => c switch
    {
        >= 'a' and <= 'z' => char.ToUpperInvariant(c),
        '1' => '!', '2' => '@', '3' => '#', '4' => '$', '5' => '%',
        '6' => '^', '7' => '&', '8' => '*', '9' => '(', '0' => ')',
        '`' => '~', '-' => '_', '=' => '+', '[' => '{', ']' => '}',
        '\\' => '|', ';' => ':', '\'' => '"', ',' => '<', '.' => '>', '/' => '?',
        _ => c,
    };

    // ── Time ──

    /// <summary>Advance the engine clock by the given milliseconds —
    /// typeahead timeouts elapse and tickers fire, instantly and
    /// deterministically.</summary>
    public void Wait(ulong milliseconds) => App.SetNow(App.Now + milliseconds);

    // ── Scenarios ──

    /// <summary>Run a scenario file against the app from its current
    /// state. Throws <see cref="ScenarioException"/>, with the file path
    /// and line number, on the first failed assertion.</summary>
    public void RunScenario(string path) =>
        Scenario.Parse(File.ReadAllText(path), path).Run(this);

    /// <summary>Run scenario text against the app from its current state.</summary>
    public void RunScenarioText(string text, string sourceName = "<inline>") =>
        Scenario.Parse(text, sourceName).Run(this);

    /// <summary>Run a scenario file's inputs and rewrite the file with
    /// the utterances actually heard: input lines and comments survive
    /// verbatim, say/nospeech lines are regenerated. The re-recorded
    /// file replays cleanly; read it to approve the behavior it froze.</summary>
    public void RecordScenario(string path) =>
        File.WriteAllText(path, Scenario.Parse(File.ReadAllText(path), path).Record(this));

    /// <summary>Run scenario text's inputs and return the re-recorded
    /// scenario with the utterances actually heard filled in.</summary>
    public string RecordScenarioText(string text, string sourceName = "<inline>") =>
        Scenario.Parse(text, sourceName).Record(this);
}
