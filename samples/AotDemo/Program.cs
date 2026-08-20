// The Native AOT canary: the smallest full-stack srui application,
// published with PublishAot to prove the toolkit — and the Srui.Testing
// harness — compiles and runs ahead-of-time. Windowed by default — a
// window, three widgets, and speech, like the other demos. With
// --headless it instead replays an embedded scenario against a headless
// TestApp, prints every utterance as it is heard, and exits nonzero on
// the first divergence; that mode needs no window, no speech, and no
// native DLLs, so it doubles as a terminal-verifiable AOT smoke test.
// --headless --record prints the scenario re-recorded from the current
// behavior instead, for pasting back into HeadlessSmoke.Scenario after
// an intentional change.

using Srui;

if (args.Contains("--headless"))
    return HeadlessSmoke.Run(record: args.Contains("--record"));

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

/// <summary>Prints every utterance as it is delivered, so the smoke's
/// output is the full transcript even when the run fails early.</summary>
internal sealed class PrintingReader : IReader
{
    public void OnEvent(AccessibilityEvent e)
    {
        if (SpeechRenderer.RenderEvent(e) is string s)
            Console.WriteLine(s);
    }
}

internal static class HeadlessSmoke
{
    // The frozen exchange: focus lands on the name field, its text is
    // replaced, Shout toggles on with Space, and Greet announces the
    // assembled greeting. Regenerate with --headless --record after an
    // intentional behavior change, and read the diff as the review.
    private const string Scenario = """
        say Name edit selected world
        s+end
        say Already selected to bottom, d
        type AOT
        say Selection removed
        say cap A
        say cap O
        say cap T
        tab
        say Shout check box not checked
        space
        say checked
        tab
        say Greet button
        enter
        say HELLO, AOT!
        """;

    public static int Run(bool record)
    {
        using var ui = new Srui.Testing.TestApp(app => Program.BuildUi(app));
        if (record)
        {
            Console.Write(ui.RecordScenarioText(Scenario));
            return 0;
        }
        ui.App.AddReader(new PrintingReader());
        try
        {
            ui.RunScenarioText(Scenario, "embedded scenario");
        }
        catch (Srui.Testing.SruiAssertException ex)
        {
            Console.WriteLine(ex.Message);
            Console.WriteLine("headless smoke: FAILED");
            return 1;
        }
        Console.WriteLine("headless smoke: ok");
        return 0;
    }
}
