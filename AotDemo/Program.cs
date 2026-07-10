// The Native AOT canary: the smallest full-stack srui application,
// published with PublishAot to prove the toolkit compiles and runs
// ahead-of-time. Windowed by default — a window, three widgets, and
// speech, like the other demos. With --headless it instead drives a
// headless SruiApp through a scripted exchange, prints every utterance
// a recording reader heard, and exits nonzero if key utterances are
// missing; that mode needs no window, no speech, and no native DLLs,
// so it doubles as a terminal-verifiable AOT smoke test.

using Srui;

if (args.Contains("--headless"))
    return HeadlessSmoke.Run();

using var app = new SruiApp("SRUI AOT Demo");
Console.WriteLine($"speech backend: {app.Voice?.BackendName}");

Program.BuildUi(app);
app.Run();
return 0;

internal partial class Program
{
    // The shared little UI: a name field, a shout toggle, and a greet
    // button that announces the assembled greeting. Quit closes the app.
    internal static (EditBox Name, CheckBox Shout, Button Greet) BuildUi(SruiApp app)
    {
        var name = new EditBox(app, "Name", "world");
        var shout = new CheckBox(app, "Shout");
        var greet = new Button(app, "Greet");
        var quit = new Button(app, "Quit");
        greet.Activated += () =>
        {
            var greeting = $"Hello, {name.Text}!";
            app.Announce(shout.Checked ? greeting.ToUpperInvariant() : greeting);
        };
        quit.Activated += app.Quit;
        return (name, shout, greet);
    }
}

internal sealed class RecordingReader : IReader
{
    public readonly List<AccessibilityEvent> Events = new();

    public void OnEvent(AccessibilityEvent e) => Events.Add(e);
}

internal static class HeadlessSmoke
{
    public static int Run()
    {
        using var app = SruiApp.Headless();
        var reader = new RecordingReader();
        app.AddReader(reader);
        Program.BuildUi(app);

        var transcript = new List<string>();
        // Deliver after every step: utterances a later input would
        // coalesce away are part of the exchange here.
        void Drain()
        {
            app.DispatchEvents();
            transcript.AddRange(reader.Events
                .Select(SpeechRenderer.RenderEvent)
                .OfType<string>());
            reader.Events.Clear();
        }
        void Push(InputEvent ev)
        {
            app.HandleInput(ev);
            Drain();
        }

        // Focus the name field, replace its text, toggle shout on with
        // Space, and press the greet button.
        app.EnsureFocus();
        Drain();
        Push(InputEvent.Simple(InputKind.SelectToLineEnd));
        foreach (var c in "AOT")
            Push(InputEvent.TypeChar(c));
        Push(InputEvent.Simple(InputKind.NavigateNext));
        Push(InputEvent.TypeChar(' '));
        Push(InputEvent.Simple(InputKind.NavigateNext));
        Push(InputEvent.Simple(InputKind.Activate));

        foreach (var line in transcript)
            Console.WriteLine(line);

        // The load-bearing moments of the exchange: the toggle spoken
        // from live widget state, and the announcement assembled from
        // the edited text and the checked box.
        string[] expected = { "Shout check box", "checked", "HELLO, AOT!" };
        var missing = expected
            .Where(e => !transcript.Any(t => t.Contains(e)))
            .ToList();
        if (missing.Count == 0)
        {
            Console.WriteLine("headless smoke: ok");
            return 0;
        }
        Console.WriteLine($"headless smoke: MISSING {string.Join(", ", missing)}");
        return 1;
    }
}
