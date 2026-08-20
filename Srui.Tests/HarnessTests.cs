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
        ui.Drain();

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
        ui.Drain();

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
        ui.Drain();

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
        ui.Drain();

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
        ui.Drain();

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
        ui.Drain();

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
        ui.Drain();
        Assert.Equal(before + 250, ui.App.Now);
        Assert.True(ticks > 0);
    }

    [Fact]
    public void TypeStringDeliversWholeCodepoints()
    {
        using var ui = new TestApp();
        var edit = new EditBox(ui.App, "Name", "");
        edit.Focus();
        ui.Drain();

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
