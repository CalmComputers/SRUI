using Srui.Testing;
using Xunit;

namespace MyApp.Tests;

public class GreetTests
{
    /// <summary>The app under test: the same UI the window runs,
    /// built headless with a recording reader attached.</summary>
    private static TestApp Start() => new(Ui.Build);

    [Fact]
    public void FocusLandsOnTheNameField()
    {
        using var ui = Start();
        ui.Expect("Your name edit blank");
    }

    [Fact]
    public void GreetsTheTypedName()
    {
        using var ui = Start();
        ui.Drain();
        ui.Type("Ada");
        ui.Drain();               // the character echoes are not the point here
        ui.Press("enter");        // Enter anywhere presses Greet
        ui.Expect("Hello, Ada.");
    }

    [Fact]
    public void GreetsTheStrangerWhenBlank()
    {
        using var ui = Start();
        ui.Drain();
        ui.Press("enter");
        ui.Expect("Hello, stranger.");
    }

    [Fact]
    public void TheRecordedExchangeStillHolds()
    {
        using var ui = Start();
        ui.RunScenario(Path.Combine("scenarios", "greet.srs"));
    }
}
