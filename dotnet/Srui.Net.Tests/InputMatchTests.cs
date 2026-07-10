using Srui;
using Xunit;

namespace Srui.Net.Tests;

/// <summary>InputEvent.Is/IsChar — combo matching by physical intent
/// (provenance when stamped, canonical fallback when synthetic) and
/// full-codepoint character comparison.</summary>
public class InputMatchTests
{
    private static InputEvent WithProvenance(InputKind kind, KeyCombo combo)
    {
        var (key, mods) = combo.ToFlat();
        return new InputEvent(kind, 0, key, mods);
    }

    [Fact]
    public void IsComboMatchesStampedProvenance()
    {
        var shiftUp = WithProvenance(InputKind.SelectLineUp, KeyCombo.WithShift(Key.Up));
        Assert.True(shiftUp.Is(KeyCombo.WithShift(Key.Up)));
        Assert.False(shiftUp.Is(KeyCombo.WithShift(Key.Down)));
        Assert.False(shiftUp.Is(Key.Up));
    }

    [Fact]
    public void IsComboPrefersProvenanceOverTheCanonicalMap()
    {
        // Shift+Backspace and Ctrl+Backspace both map to word-delete;
        // the stamped combo, not the canonical reverse map, must win.
        var shiftBackspace = WithProvenance(
            InputKind.DeleteWordBackward, KeyCombo.WithShift(Key.Backspace));
        Assert.True(shiftBackspace.Is(KeyCombo.WithShift(Key.Backspace)));
        Assert.False(shiftBackspace.Is(KeyCombo.WithCtrl(Key.Backspace)));
    }

    [Fact]
    public void IsComboFallsBackForSyntheticInputs()
    {
        Assert.True(InputEvent.Simple(InputKind.SelectLineUp).Is(KeyCombo.WithShift(Key.Up)));
        Assert.True(InputEvent.Simple(InputKind.DeleteForward).Is(Key.Delete));
        Assert.True(InputEvent.Simple(InputKind.MoveLeft).Is(Key.Left));
        Assert.False(InputEvent.Simple(InputKind.MoveLeft).Is(Key.Right));
    }

    [Fact]
    public void IsCharComparesTheFullCodepoint()
    {
        Assert.True(InputEvent.TypeChar(' ').IsChar(' '));
        Assert.False(InputEvent.TypeChar('x').IsChar(' '));
        Assert.False(InputEvent.Simple(InputKind.Activate).IsChar(' '));
        // U+10020 truncates to 0x0020 under a (char) cast; the full
        // comparison must not alias it to space.
        var astral = new InputEvent(InputKind.TypeChar, 0x10020, 0, Mods.None);
        Assert.False(astral.IsChar(' '));
    }

    [Fact]
    public void AstralCharacterDoesNotToggleACheckBox()
    {
        using var ui = new TestUi();
        var box = new CheckBox(ui.App, "Wrap");
        box.Focus();
        ui.Drain();

        ui.Input(new InputEvent(InputKind.TypeChar, 0x10020, 0, Mods.None));
        Assert.False(box.Checked);
        Assert.Empty(ui.Spoken());
    }

    [Fact]
    public void AstralCharacterDoesNotPressAButton()
    {
        using var ui = new TestUi();
        var button = new Button(ui.App, "Save");
        var pressed = 0;
        button.Activated += () => pressed++;
        button.Focus();
        ui.Drain();

        ui.Input(new InputEvent(InputKind.TypeChar, 0x10020, 0, Mods.None));
        ui.Drain();
        Assert.Equal(0, pressed);
    }
}
