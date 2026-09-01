using Srui;
using Xunit;

namespace Srui.Tests;

public class ComboSpecTests
{
    private static KeyCombo Parse(string spec)
    {
        Assert.True(ComboSpec.TryParse(spec, out var combo), $"\"{spec}\" should parse");
        return combo;
    }

    [Fact]
    public void CompactClusterExpandsLetterwise()
    {
        Assert.Equal(new KeyCombo(Key.F(4), true, true, true), Parse("cas+f4"));
        Assert.Equal(new KeyCombo(Key.Enter, false, true, true), Parse("as+enter"));
        Assert.Equal(KeyCombo.WithCtrl(Key.Char('s')), Parse("c+s"));
    }

    [Fact]
    public void FinalSegmentIsAlwaysTheKey()
    {
        Assert.Equal(KeyCombo.Plain(Key.Char('s')), Parse("s"));
        Assert.Equal(KeyCombo.Plain(Key.Char('c')), Parse("c"));
        Assert.Equal(new KeyCombo(Key.Char('s'), false, false, true), Parse("s+s"));
        Assert.Equal(KeyCombo.Plain(Key.Down), Parse("down"));
    }

    [Fact]
    public void NamedAndCompactFormsMix()
    {
        Assert.Equal(KeyCombo.CtrlShift(Key.Char('t')), Parse("ctrl+shift+t"));
        Assert.Equal(KeyCombo.CtrlShift(Key.Char('t')), Parse("c+shift+t"));
        Assert.Equal(KeyCombo.WithCtrl(Key.Escape), Parse("control+esc"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("foo")]
    [InlineData("cq+s")]
    [InlineData("+s")]
    [InlineData("c+")]
    public void MalformedSpecsAreRejected(string spec) =>
        Assert.False(ComboSpec.TryParse(spec, out _));

    [Fact]
    public void ParseThrowsOnFailure() =>
        Assert.Throws<ArgumentException>(() => ComboSpec.Parse("not a combo"));
}

public class KeyTapSimulationTests
{
    [Fact]
    public void PressTabRunsTheHostMapping()
    {
        using var ui = new TestApp();
        _ = new Button(ui.App, "One");
        var two = new Button(ui.App, "Two");
        ui.App.EnsureFocus();

        ui.Press("tab");
        Assert.True(two.IsFocused);
        Assert.Equal(new[] { "Two button" }, ui.Spoken());
    }

    [Fact]
    public void PressPrintableTypesWithUsShiftLayout()
    {
        using var ui = new TestApp();
        var edit = new EditBox(ui.App, "Name", "");
        edit.Focus();

        ui.Press("a");
        ui.Press("s+a");
        ui.Press("s+1");
        Assert.Equal("aA!", edit.Text);
    }

    [Fact]
    public void PressSpaceTogglesCheckBox()
    {
        using var ui = new TestApp();
        var box = new CheckBox(ui.App, "Wrap");
        box.Focus();

        ui.Press("space");
        Assert.True(box.Checked);
    }

    [Fact]
    public void PressUnmappedComboMatchesWidgetShortcuts()
    {
        using var ui = new TestApp();
        _ = new Button(ui.App, "One");
        var two = new Button(ui.App, "Two");
        two.AddShortcut(ComboSpec.Parse("c+g"));
        ui.App.EnsureFocus();

        ui.Press("c+g");
        Assert.True(two.IsFocused);
    }

    [Fact]
    public void DownAndUpDriveBindKeyPhases()
    {
        using var ui = new TestApp();
        var button = new Button(ui.App, "Go");
        var phases = new List<string>();
        button.BindKey(KeyCombo.Plain(Key.Char('j')), KeyPhase.Press, () => phases.Add("press"));
        button.BindKey(KeyCombo.Plain(Key.Char('j')), KeyPhase.Release, () => phases.Add("release"));
        button.Focus();

        ui.Down("j");
        Assert.Equal(new[] { "press" }, phases);
        ui.Up("j");
        Assert.Equal(new[] { "press", "release" }, phases);
    }

    [Fact]
    public void PressDeliversPhasesAroundTheLogicalInput()
    {
        using var ui = new TestApp();
        var edit = new EditBox(ui.App, "Name", "");
        var order = new List<string>();
        edit.BindKey(KeyCombo.Plain(Key.Char('x')), KeyPhase.Press,
            () => order.Add($"press:{edit.Text}"));
        edit.BindKey(KeyCombo.Plain(Key.Char('x')), KeyPhase.Release,
            () => order.Add($"release:{edit.Text}"));
        edit.Focus();

        ui.Press("x");
        // The press phase sees the text before the typed character
        // lands; the release phase sees it after — host order.
        Assert.Equal(new[] { "press:", "release:x" }, order);
    }

    [Fact]
    public void WaitAdvancesTheClockAndFiresTickers()
    {
        using var ui = new TestApp();
        _ = new Button(ui.App, "One");
        var ticks = 0;
        ui.App.StartTicker(100).Tick += () => ticks++;
        ui.Drain();

        var before = ui.App.Now;
        ui.Wait(250);
        Assert.Equal(before + 250, ui.App.Now);
        Assert.True(ticks > 0);
    }

    [Fact]
    public void TypeStringDeliversWholeCodepoints()
    {
        using var ui = new TestApp();
        var edit = new EditBox(ui.App, "Name", "");
        edit.Focus();

        ui.Type("a\U0001F600b");
        Assert.Equal("a\U0001F600b", edit.Text);
    }
}

public class ExpectTests
{
    [Fact]
    public void ExpectPassesOnExactOrderedBatch()
    {
        using var ui = new TestApp();
        _ = new Button(ui.App, "Save");
        ui.App.EnsureFocus();
        ui.Expect("Save button");
        ui.ExpectNoSpeech();
    }

    [Fact]
    public void ExpectFailsWithReadableMessage()
    {
        using var ui = new TestApp();
        _ = new Button(ui.App, "Save");
        ui.App.EnsureFocus();

        var ex = Assert.Throws<SruiAssertException>(() => ui.Expect("Load button"));
        Assert.Contains("\"Load button\"", ex.Message);
        Assert.Contains("\"Save button\"", ex.Message);
    }

    [Fact]
    public void ExpectNoSpeechFailsWhenSomethingWasSaid()
    {
        using var ui = new TestApp();
        _ = new Button(ui.App, "Save");
        ui.App.EnsureFocus();

        var ex = Assert.Throws<SruiAssertException>(ui.ExpectNoSpeech);
        Assert.Contains("\"Save button\"", ex.Message);
    }

    [Fact]
    public void BuilderConstructorEstablishesFocus()
    {
        using var ui = new TestApp(app => _ = new Button(app, "Save"));
        ui.Expect("Save button");
    }
}

public class StepTests
{
    [Fact]
    public void AStepDiscardsThePreviousBatch()
    {
        using var ui = new TestApp(app =>
        {
            _ = new Button(app, "One");
            _ = new Button(app, "Two");
        });
        // The focus announcement from construction is never asserted:
        // the first step drops it, and Expect sees the step alone.
        ui.Press("tab");
        ui.Expect("Two button");
    }

    [Fact]
    public void AStepDispatchesBeforeReturning()
    {
        using var ui = new TestApp();
        var below = new Button(ui.App, "Below");
        below.Focus();
        var dialog = ui.App.OpenDialog();
        new Button(dialog, "Inside").Focus();

        // Closing a dialog restores focus at drain time; the step has
        // drained by the time it returns, so the restored focus is
        // assertable at once (the restore itself is silent — nothing
        // changed under the dialog).
        ui.Press("escape");
        Assert.True(below.IsFocused);
        ui.ExpectNoSpeech();
    }

    [Fact]
    public void TypingAStringIsOneStep()
    {
        using var ui = new TestApp();
        new EditBox(ui.App, "Name").Focus();
        ui.Type("ab");
        ui.Expect("a", "b");
    }

    [Fact]
    public void ProgrammaticMutationsShareABatchUntilDrained()
    {
        using var ui = new TestApp();
        var save = new Button(ui.App, "Save");
        save.Focus();
        ui.Drain();

        // No step separates these, so one batch holds both deltas —
        // Drain is the boundary a mutation has to draw for itself.
        save.Name = "Save All";
        save.Description = "saves the file";
        ui.Expect("Save All", "saves the file");
    }
}

public class TestHostTests
{
    private static (TestHost Ui, HostedApp A, HostedApp B) TwoApps()
    {
        var ui = new TestHost();
        var a = ui.Host.Add("Alpha");
        _ = new Button(a.App, "First");
        var b = ui.Host.Add("Beta");
        _ = new Button(b.App, "Second");
        ui.Host.Activate(a);
        return (ui, a, b);
    }

    [Fact]
    public void PressReachesTheHostSoSwitchingCombosWork()
    {
        var (ui, _, _) = TwoApps();
        using var _ = ui;
        ui.Press("ctrl+tab");
        ui.Expect("Beta", "Second button");
    }

    [Fact]
    public void WaitAdvancesEveryAppsClockAndFiresTickers()
    {
        var (ui, a, b) = TwoApps();
        using var _ = ui;
        var ticks = 0;
        b.App.StartTicker(100).Tick += () => ticks++;
        ui.Wait(250);
        Assert.Equal(250UL, ui.Host.Now);
        Assert.Equal(250UL, a.App.Now);
        Assert.Equal(250UL, b.App.Now);
        Assert.True(ticks > 0);
    }

    [Fact]
    public void AnAppAddedLaterJoinsTheHostsClock()
    {
        var (ui, _, _) = TwoApps();
        using var _ = ui;
        ui.Wait(1000);
        var late = ui.Host.Add("Gamma");
        Assert.Equal(1000UL, late.App.Now);
    }

    [Fact]
    public void TickFoldsAHostedQuitIntoClose()
    {
        var (ui, a, b) = TwoApps();
        using var _ = ui;
        ui.Drain();
        a.App.Quit();
        ui.Tick();
        Assert.True(a.IsClosed);
        Assert.Same(b, ui.Host.Active);
        ui.Expect("Beta", "Second button");
    }

    [Fact]
    public void TickRunsTheHostsOwnWork()
    {
        var (ui, _, _) = TwoApps();
        using var _ = ui;
        var ran = 0;
        ui.Host.Ticked = () => ran++;
        ui.Tick();
        Assert.Equal(1, ran);
    }

    [Fact]
    public void UntilReturnsWhenTheConditionHoldsAndKeepsWhatWasSpoken()
    {
        var (ui, a, _) = TwoApps();
        using var _ = ui;
        var landed = false;
        var ticker = a.App.StartTicker(1);
        ticker.Tick += () =>
        {
            if (landed)
                return;
            landed = true;
            a.App.Announce("landed");
        };
        ui.Until(() => landed, "the ticker");
        ui.Expect("landed");
    }

    [Fact]
    public void UntilTimesOutNamingWhatItWaitedFor()
    {
        var (ui, a, _) = TwoApps();
        using var _ = ui;
        var said = false;
        a.App.StartTicker(1).Tick += () =>
        {
            if (!said)
                a.App.Announce("still here");
            said = true;
        };
        var e = Assert.Throws<SruiAssertException>(
            () => ui.Until(() => false, "a result that never comes", timeoutMs: 20));
        Assert.Equal(
            "timed out after 20 ms waiting for a result that never comes; heard \"still here\"",
            e.Message);
    }

    [Fact]
    public void UntilHeardWatchesTheBatch()
    {
        var (ui, a, _) = TwoApps();
        using var _ = ui;
        var count = 0;
        a.App.StartTicker(1).Tick += () =>
        {
            if (++count == 3)
                a.App.Announce("third");
        };
        ui.UntilHeard(u => u == "third", "the third tick");
        ui.Expect("third");
    }

    [Fact]
    public void ScenariosRunAgainstAHost()
    {
        var (ui, _, _) = TwoApps();
        using var _ = ui;
        ui.RunScenarioText("""
            say Alpha
            say First button
            ctrl+tab
            say Beta
            say Second button
            """);
    }
}

public class WindowsKeyComboSpecTests
{
    [Fact]
    public void CompactInitialsIncludeWin()
    {
        Assert.Equal(KeyCombo.WinAlt(Key.Space), ComboSpec.Parse("wa+space"));
        Assert.Equal(KeyCombo.WithWin(Key.Char('k')), ComboSpec.Parse("win+k"));
        Assert.Equal(new KeyCombo(Key.F(1), true, false, false, true), ComboSpec.Parse("windows+ctrl+f1"));
    }
}
