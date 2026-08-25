namespace Srui.Testing;

/// <summary>A scenario assertion or parse failure. The message begins
/// with the scenario source and line number.</summary>
public sealed class ScenarioException : SruiAssertException
{
    /// <summary>Create with the full failure message.</summary>
    public ScenarioException(string message) : base(message) { }
}

/// <summary>A parsed scenario: a plain-text recording of an exchange with
/// an app, run against a <see cref="Harness"/> (see
/// <see cref="Harness.RunScenario"/>) or re-recorded from one (see
/// <see cref="Harness.RecordScenario"/>).
///
/// The format is line-oriented, one step per line. A bare line taps a key
/// combo (<see cref="ComboSpec"/> syntax: "enter", "cas+f4"). The verbs:
/// <c>down</c>/<c>up</c> hold and release a combo, <c>type</c> types the
/// rest of the line, <c>wait</c> advances the clock by milliseconds,
/// <c>say</c> asserts the next utterance of the current batch exactly,
/// <c>nospeech</c> asserts the batch is empty. <c>//</c> starts a
/// comment. Each input step drains speech into a fresh batch; a batch
/// with no assertions after it is discarded unchecked, while a batch
/// with any must be asserted completely. Anything richer than exact
/// utterance matching belongs in C# against the harness, not here.</summary>
internal sealed class Scenario
{
    private abstract record Step(int Line, string Raw)
    {
        internal sealed record Tap(int Line, string Raw, KeyCombo Combo) : Step(Line, Raw);
        internal sealed record Down(int Line, string Raw, KeyCombo Combo) : Step(Line, Raw);
        internal sealed record Up(int Line, string Raw, KeyCombo Combo) : Step(Line, Raw);
        internal sealed record Type(int Line, string Raw, string Text) : Step(Line, Raw);
        internal sealed record Wait(int Line, string Raw, ulong Ms) : Step(Line, Raw);
        internal sealed record Say(int Line, string Raw, string Utterance) : Step(Line, Raw);
        internal sealed record NoSpeech(int Line, string Raw) : Step(Line, Raw);
        internal sealed record Trivia(int Line, string Raw) : Step(Line, Raw);
    }

    private readonly List<Step> _steps;
    private readonly string _source;

    private Scenario(List<Step> steps, string source)
    {
        _steps = steps;
        _source = source;
    }

    /// <summary>Parse scenario text. Throws <see cref="ScenarioException"/>
    /// on the first malformed line.</summary>
    public static Scenario Parse(string text, string source)
    {
        var steps = new List<Step>();
        var lines = text.Split('\n');
        // A trailing newline yields one final empty fragment, not a line.
        var count = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;

        for (var i = 0; i < count; i++)
        {
            var lineNo = i + 1;
            var raw = lines[i].TrimEnd();
            var body = raw.TrimStart();

            if (body.Length == 0 || body.StartsWith("//", StringComparison.Ordinal))
            {
                steps.Add(new Step.Trivia(lineNo, raw));
                continue;
            }

            var space = body.IndexOf(' ');
            var verb = space < 0 ? body : body[..space];
            var arg = space < 0 ? "" : body[(space + 1)..];

            switch (verb)
            {
                case "say":
                    if (arg.Length == 0)
                        throw Fail(source, lineNo, "say needs an utterance");
                    steps.Add(new Step.Say(lineNo, raw, arg));
                    break;
                case "nospeech" when arg.Length == 0:
                    steps.Add(new Step.NoSpeech(lineNo, raw));
                    break;
                case "type":
                    if (arg.Length == 0)
                        throw Fail(source, lineNo, "type needs text");
                    steps.Add(new Step.Type(lineNo, raw, arg));
                    break;
                case "wait":
                    if (!ulong.TryParse(arg, out var ms))
                        throw Fail(source, lineNo, $"wait needs a millisecond count, not \"{arg}\"");
                    steps.Add(new Step.Wait(lineNo, raw, ms));
                    break;
                // A bare "down" or "up" is not the verb but the arrow key:
                // the guard lets it fall through to the combo default.
                case "down" when arg.Length != 0:
                case "up" when arg.Length != 0:
                    if (!ComboSpec.TryParse(arg, out var held))
                        throw Fail(source, lineNo, $"unparseable key combo \"{arg}\"");
                    steps.Add(verb == "down"
                        ? new Step.Down(lineNo, raw, held)
                        : new Step.Up(lineNo, raw, held));
                    break;
                default:
                    if (!ComboSpec.TryParse(body, out var combo))
                        throw Fail(source, lineNo, $"not a verb or key combo: \"{body}\"");
                    steps.Add(new Step.Tap(lineNo, raw, combo));
                    break;
            }
        }
        return new Scenario(steps, source);
    }

    /// <summary>Replay against the harness from its current state,
    /// throwing on the first failed assertion.</summary>
    public void Run(Harness ui)
    {
        // The batch pending before the first input is assertable too —
        // say lines above all inputs freeze startup speech.
        var batch = ui.Spoken();
        var cursor = 0;
        var asserted = false;

        foreach (var step in _steps)
        {
            switch (step)
            {
                case Step.Trivia:
                    break;
                case Step.Say say:
                    asserted = true;
                    if (cursor >= batch.Count)
                        throw Fail(_source, say.Line,
                            $"expected \"{say.Utterance}\" but nothing more was spoken");
                    if (!string.Equals(batch[cursor], say.Utterance, StringComparison.Ordinal))
                        throw Fail(_source, say.Line,
                            $"expected \"{say.Utterance}\" but heard \"{batch[cursor]}\"");
                    cursor++;
                    break;
                case Step.NoSpeech noSpeech:
                    asserted = true;
                    if (batch.Count != 0)
                        throw Fail(_source, noSpeech.Line,
                            $"expected no speech but heard {Describe(batch, 0)}");
                    break;
                default:
                    // An asserted batch must be asserted completely;
                    // an unasserted one is out of scope and discarded.
                    if (asserted && cursor < batch.Count)
                        throw Fail(_source, step.Line,
                            $"unasserted speech before this step: {Describe(batch, cursor)}");
                    Execute(ui, step);
                    batch = ui.Spoken();
                    cursor = 0;
                    asserted = false;
                    break;
            }
        }
        if (asserted && cursor < batch.Count)
            throw Fail(_source, _steps[^1].Line,
                $"unasserted speech at the end: {Describe(batch, cursor)}");
    }

    /// <summary>Run the inputs and return the scenario re-recorded with
    /// the utterances actually heard: trivia and input lines survive
    /// verbatim, assertion lines are regenerated — a say per utterance,
    /// nospeech for a silent batch. Startup speech, when present, is
    /// recorded as say lines above the first input.</summary>
    public string Record(Harness ui)
    {
        var output = new List<string>();
        var startup = ui.Spoken();
        var startupEmitted = false;

        foreach (var step in _steps)
        {
            switch (step)
            {
                case Step.Trivia trivia:
                    output.Add(trivia.Raw);
                    break;
                case Step.Say or Step.NoSpeech:
                    break;
                default:
                    if (!startupEmitted)
                    {
                        output.AddRange(startup.Select(u => $"say {u}"));
                        startupEmitted = true;
                    }
                    Execute(ui, step);
                    var batch = ui.Spoken();
                    output.Add(step.Raw.TrimStart());
                    if (batch.Count == 0)
                        output.Add("nospeech");
                    else
                        output.AddRange(batch.Select(u => $"say {u}"));
                    break;
            }
        }
        if (!startupEmitted)
            output.AddRange(startup.Select(u => $"say {u}"));
        return string.Join("\n", output) + "\n";
    }

    private static void Execute(Harness ui, Step step)
    {
        switch (step)
        {
            case Step.Tap tap: ui.Press(tap.Combo); break;
            case Step.Down down: ui.Down(down.Combo); break;
            case Step.Up up: ui.Up(up.Combo); break;
            case Step.Type type: ui.Type(type.Text); break;
            case Step.Wait wait: ui.Wait(wait.Ms); break;
        }
    }

    private static string Describe(IReadOnlyList<string> batch, int from) =>
        string.Join(", ", batch.Skip(from).Select(u => $"\"{u}\""));

    private static ScenarioException Fail(string source, int line, string message) =>
        new($"{source}:{line}: {message}");
}
