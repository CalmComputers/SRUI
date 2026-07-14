using Srui;
using Xunit;

namespace Srui.Net.Tests;

/// <summary>Widget.KeyHelp: the "with help" state, the F1 status dialog,
/// and the key reservation that rides along.</summary>
public class KeyHelpTests
{
    [Fact]
    public void HelpAppearsInFocusAnnouncement()
    {
        using var ui = new TestUi();
        var button = new Button(ui.App, "Roll") { KeyHelp = "R rolls twice." };
        button.Focus();
        Assert.Equal(new[] { "Roll button with help" }, ui.Spoken());
    }

    [Fact]
    public void TogglingHelpWhileFocusedSpeaksTheTransition()
    {
        using var ui = new TestUi();
        var button = new Button(ui.App, "Roll");
        button.Focus();
        ui.Drain();

        button.KeyHelp = "R rolls twice.";
        Assert.Equal(new[] { "with help" }, ui.Spoken());

        // Text replacement while present stays silent.
        button.KeyHelp = "R rolls three times.";
        Assert.Empty(ui.Spoken());

        button.KeyHelp = null;
        Assert.Equal(new[] { "help removed" }, ui.Spoken());
    }

    [Fact]
    public void F1OpensAReviewableHelpDialogAndEscapeReturns()
    {
        using var ui = new TestUi();
        var list = new ListBox(ui.App, "To-do", ["a"]) { KeyHelp = "Space marks done." };
        list.Focus();
        ui.Drain();

        Assert.True(ui.Raw(KeyCombo.Plain(Key.F(1))));
        var spoken = ui.Spoken();
        Assert.Contains(spoken, s => s.Contains("Help") && s.Contains("Space marks done."));

        // Escape closes the dialog and focus returns to the list.
        ui.Input(InputKind.Dismiss);
        ui.Drain();
        Assert.True(list.IsFocused);
    }

    [Fact]
    public void F1FallsThroughWithoutHelp()
    {
        using var ui = new TestUi();
        var button = new Button(ui.App, "Roll");
        button.Focus();
        ui.Drain();
        Assert.False(ui.Raw(KeyCombo.Plain(Key.F(1))));
    }

    [Fact]
    public void WidgetOwnInputBeatsTheHelpBinding()
    {
        using var ui = new TestUi();
        var widget = new F1Eater(ui.App) { KeyHelp = "Never shown." };
        widget.Focus();
        ui.Drain();

        Assert.True(ui.Raw(KeyCombo.Plain(Key.F(1))));
        Assert.Equal(new[] { "ate F1" }, ui.Spoken());
    }

    [Fact]
    public void HelpReservesF1AcrossWidgetKinds()
    {
        using var ui = new TestUi();
        var f1 = KeyCombo.Plain(Key.F(1));

        var button = new Button(ui.App, "Roll");
        Assert.False(button.ReservesKey(f1));
        button.KeyHelp = "R rolls twice.";
        Assert.True(button.ReservesKey(f1));
        Assert.False(button.ReservesKey(KeyCombo.WithShift(Key.F(1))));
        Assert.False(button.ReservesKey(KeyCombo.Plain(Key.F(2))));

        // The built-in overrides keep the base call.
        var edit = new EditBox(ui.App, "Notes") { KeyHelp = "Up recalls history." };
        Assert.True(edit.ReservesKey(f1));
    }

    private sealed class F1Eater : CustomWidget
    {
        public F1Eater(IWidgetContainer parent) : base(parent, "Eater") { }

        protected override bool OnInput(in InputEvent input)
        {
            if (input.Is(Key.F(1)))
            {
                Announce("ate F1");
                return true;
            }
            return base.OnInput(input);
        }
    }
}
