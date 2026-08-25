using System.Diagnostics;
using Srui.Core;

namespace Srui.Testing;

/// <summary>A speech assertion failed. Carries a message readable as
/// plain text: what was expected, what was actually spoken.</summary>
public class SruiAssertException : Exception
{
    /// <summary>Create with the full failure message.</summary>
    public SruiAssertException(string message) : base(message) { }
}

/// <summary>What every harness shares: a recording reader, batches,
/// steps, assertions, input at three fidelities, time, and scenarios.
/// <see cref="TestApp"/> is this over one headless <see cref="SruiApp"/>;
/// <see cref="TestHost"/> is this over a headless
/// <see cref="MultiAppHost"/>, and a shell's own harness derives from
/// that. A scenario runs against either.
///
/// Every input method (and <see cref="Wait"/>) is a step: it discards
/// whatever the previous step spoke, runs, and dispatches — so deferred
/// work has happened and state can be asserted when it returns,
/// <see cref="Expect"/> always describes the last step alone, and speech
/// a test never asks about is out of its scope: the same discipline as
/// a scenario file. Only programmatic mutations, which open no step,
/// need an explicit <see cref="Drain"/> between them.
/// Input methods simulate a real host faithfully: <see cref="Press(KeyCombo)"/>
/// runs the same physical-to-logical mapping the SDL host runs, delivers
/// the physical key phases around the logical input in host order, and
/// synthesizes the TypeChar an unmodified printable key produces.
/// Combo strings accept both the config form ("ctrl+shift+t") and
/// compact modifier initials ("cs+t"); see <see cref="ComboSpec"/>.</summary>
public abstract class Harness : IDisposable
{
    /// <summary>The recording reader attached to the thing under test.</summary>
    public RecordingReader Reader { get; } = new();

    /// <inheritdoc/>
    public abstract void Dispose();

    // ── What a derived harness supplies ──

    /// <summary>Deliver queued output to the readers.</summary>
    protected abstract void DispatchEvents();

    /// <summary>Dispatch one logical input. True when consumed.</summary>
    protected abstract bool HandleInput(in InputEvent input);

    /// <summary>Dispatch one physical key transition. True when consumed.</summary>
    protected abstract bool HandleKey(in KeyInput key);

    /// <summary>Move the clock forward by the given milliseconds and
    /// run whatever the elapsed time owes: tickers, timeouts, one
    /// iteration of whatever loop the thing under test runs. Zero is
    /// one iteration at the same instant.</summary>
    protected abstract void Advance(ulong milliseconds);

    // ── Speech ──

    /// <summary>Deliver queued output and return the utterances heard
    /// since the last delivery, in order, rendered at full verbosity.</summary>
    public List<string> Spoken()
    {
        DispatchEvents();
        var result = Pending();
        Reader.Events.Clear();
        return result;
    }

    /// <summary>The utterances heard so far in the current batch,
    /// without delivering or clearing anything — for a predicate that
    /// watches speech arrive during <see cref="Until"/>.</summary>
    public List<string> Pending() =>
        Reader.Events
            .Select(SpeechRenderer.RenderEvent)
            .OfType<string>()
            .ToList();

    /// <summary>Deliver queued output, discarding it. Every input step
    /// does this on entry; call it yourself only to open a batch after
    /// a programmatic mutation, which has no step to open one.</summary>
    public void Drain()
    {
        DispatchEvents();
        Reader.Events.Clear();
    }

    /// <summary>One step: discard the previous batch, run the input,
    /// dispatch what it queued. The reader keeps what the dispatch
    /// delivered for the next assertion.</summary>
    protected bool Step(Func<bool> input)
    {
        Drain();
        var handled = input();
        DispatchEvents();
        return handled;
    }

    /// <summary>Assert that exactly these utterances were spoken since
    /// the last step (or delivery), in order. No arguments asserts silence
    /// (equivalent to <see cref="ExpectNoSpeech"/>).</summary>
    public void Expect(params string[] utterances)
    {
        var spoken = Spoken();
        if (spoken.SequenceEqual(utterances, StringComparer.Ordinal))
            return;
        throw new SruiAssertException(
            $"expected {Describe(utterances)} but heard {Describe(spoken)}");
    }

    /// <summary>Assert that nothing was spoken since the last step (or delivery).</summary>
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
    public bool Input(InputKind kind) => Input(InputEvent.Simple(kind));

    /// <summary>Dispatch one logical input event. True when consumed.</summary>
    public bool Input(InputEvent ev) => Step(() => HandleInput(ev));

    /// <summary>Type one character. True when consumed.</summary>
    public bool Type(char c) => Input(InputEvent.TypeChar(c));

    /// <summary>Type a string, one codepoint at a time (astral characters
    /// arrive whole, as they would from a real host). One step: the
    /// echoes of every character land in the same batch.</summary>
    public void Type(string text) => Step(() =>
    {
        foreach (var rune in text.EnumerateRunes())
            HandleInput(new InputEvent(InputKind.TypeChar, (uint)rune.Value, 0, Mods.None));
        return true;
    });

    /// <summary>Dispatch a combo as a bare RawKey input, bypassing the
    /// host mapping — for driving widget shortcuts directly. True when
    /// consumed. <see cref="Press(KeyCombo)"/> is the full key-tap
    /// simulation; prefer it for user-fidelity tests.</summary>
    public bool Raw(KeyCombo combo) => Step(() =>
    {
        var (key, mods) = combo.ToFlat();
        return HandleInput(InputEvent.RawKey(key, mods));
    });

    /// <summary>Dispatch a combo given as a string as a bare RawKey input.</summary>
    public bool Raw(string combo) => Raw(ComboSpec.Parse(combo));

    // ── Key-tap simulation ──

    /// <summary>Tap a key combo exactly as a real host would deliver it:
    /// the physical Press phase, then the mapped logical input (or the
    /// TypeChar an unmodified printable produces, uppercased and
    /// symbol-shifted under a US layout when Shift is held), then the
    /// physical Release phase. True when any stage consumed something.</summary>
    public bool Press(KeyCombo combo) => Step(() =>
    {
        var (key, mods) = combo.ToFlat();
        var handled = HandleKey(new KeyInput(key, mods, KeyPhase.Press));
        handled |= HandleLogical(combo);
        handled |= HandleKey(new KeyInput(key, mods, KeyPhase.Release));
        return handled;
    });

    /// <summary>Tap a key combo given as a string — config form
    /// ("ctrl+shift+t") or compact initials ("cs+t").</summary>
    public bool Press(string combo) => Press(ComboSpec.Parse(combo));

    /// <summary>Hold a key down: the physical Press phase plus the
    /// logical input the keydown produces, matching a real host. The key
    /// stays held until <see cref="Up(KeyCombo)"/>. Auto-repeat is not
    /// simulated. True when any stage consumed something.</summary>
    public bool Down(KeyCombo combo) => Step(() =>
    {
        var (key, mods) = combo.ToFlat();
        var handled = HandleKey(new KeyInput(key, mods, KeyPhase.Press));
        return HandleLogical(combo) | handled;
    });

    /// <summary>Hold a key down, combo given as a string.</summary>
    public bool Down(string combo) => Down(ComboSpec.Parse(combo));

    /// <summary>Release a held key: the physical Release phase.
    /// True when a handler consumed it.</summary>
    public bool Up(KeyCombo combo) => Step(() =>
    {
        var (key, mods) = combo.ToFlat();
        return HandleKey(new KeyInput(key, mods, KeyPhase.Release));
    });

    /// <summary>Release a held key, combo given as a string.</summary>
    public bool Up(string combo) => Up(ComboSpec.Parse(combo));

    private bool HandleLogical(KeyCombo combo)
    {
        if (InputMapper.MapCombo(combo) is InputEvent mapped)
            return HandleInput(mapped);
        if (TypedChar(combo) is char c)
        {
            var (key, mods) = combo.ToFlat();
            return HandleInput(new InputEvent(InputKind.TypeChar, c, key, mods));
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

    /// <summary>Advance the clock by the given milliseconds — typeahead
    /// timeouts elapse and tickers fire, instantly and deterministically.
    /// A step: what the elapsed time speaks is the next batch.</summary>
    public void Wait(ulong milliseconds) => Step(() =>
    {
        Advance(milliseconds);
        return true;
    });

    /// <summary>Run real time until a condition holds: the thing under
    /// test iterates, with its clock following the wall clock, until
    /// <paramref name="condition"/> is true, or <paramref name="timeoutMs"/>
    /// passes and the failure names <paramref name="what"/>. One step:
    /// everything spoken while waiting is the next batch. This is the
    /// one place a test waits on something outside the engine — a
    /// worker thread landing its result — and the only sanctioned use
    /// of real time; anything the engine's own clock drives is
    /// <see cref="Wait"/>.</summary>
    public void Until(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        Drain();
        var clock = Stopwatch.StartNew();
        var last = 0L;
        while (true)
        {
            var elapsed = clock.ElapsedMilliseconds;
            Advance((ulong)(elapsed - last));
            last = elapsed;
            if (condition())
                return;
            if (elapsed >= timeoutMs)
                throw new SruiAssertException(
                    $"timed out after {timeoutMs} ms waiting for {what}"
                    + (Pending() is { Count: > 0 } heard
                        ? $"; heard {Describe(heard)}"
                        : ""));
            Thread.Sleep(5);
        }
    }

    /// <summary>As <see cref="Until"/>, until an utterance satisfying
    /// <paramref name="predicate"/> has been heard in the batch.</summary>
    public void UntilHeard(Func<string, bool> predicate, string what, int timeoutMs = 5000) =>
        Until(() => Pending().Any(predicate), what, timeoutMs);

    // ── Scenarios ──

    /// <summary>Run a scenario file against the app from its current
    /// state. Throws <see cref="ScenarioException"/>, with the file path
    /// and line number, on the first failed assertion. When the
    /// SRUI_RECORD environment variable is set (non-empty, not "0"),
    /// re-records the file in place instead and passes — the one-flag
    /// re-approval pass after an intentional speech change; review the
    /// rewritten files as diffs.</summary>
    public void RunScenario(string path)
    {
        if (RecordRequested)
        {
            RecordScenario(path);
            return;
        }
        Scenario.Parse(File.ReadAllText(path), path).Run(this);
    }

    private static bool RecordRequested
    {
        get
        {
            var value = Environment.GetEnvironmentVariable("SRUI_RECORD");
            return !string.IsNullOrEmpty(value) && value != "0";
        }
    }

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
