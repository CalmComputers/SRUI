using Srui;
using Srui.Core;
using Srui.Testing;
using Xunit;

namespace Srui.Tests;

/// <summary>The Mac keyboard convention: a combo's Ctrl is the Command
/// key, Win the Control key, Alt the Option key; the macOS editing keys
/// map on top of the shared table; combos are spoken and reserved by their
/// macOS names. Each case pins the Windows behavior beside it, which the
/// convention must leave untouched.</summary>
public class MacConventionTests
{
    private static InputKind? Kind(string combo, KeyboardConvention convention) =>
        InputMapper.MapCombo(ComboSpec.Parse(combo), convention)?.Kind;

    // ── Mapping ──

    [Theory]
    [InlineData("alt+left", InputKind.MoveWordLeft)]
    [InlineData("alt+right", InputKind.MoveWordRight)]
    [InlineData("alt+shift+left", InputKind.SelectWordLeft)]
    [InlineData("alt+shift+right", InputKind.SelectWordRight)]
    [InlineData("alt+backspace", InputKind.DeleteWordBackward)]
    [InlineData("alt+delete", InputKind.DeleteWordForward)]
    [InlineData("ctrl+left", InputKind.MoveToLineStart)]
    [InlineData("ctrl+right", InputKind.MoveToLineEnd)]
    [InlineData("ctrl+shift+left", InputKind.SelectToLineStart)]
    [InlineData("ctrl+shift+right", InputKind.SelectToLineEnd)]
    [InlineData("ctrl+up", InputKind.MoveToDocStart)]
    [InlineData("ctrl+down", InputKind.MoveToDocEnd)]
    [InlineData("ctrl+shift+up", InputKind.SelectToDocStart)]
    [InlineData("ctrl+shift+down", InputKind.SelectToDocEnd)]
    [InlineData("ctrl+alt+up", InputKind.TreeUp)]
    [InlineData("ctrl+alt+down", InputKind.TreeDown)]
    [InlineData("ctrl+alt+left", InputKind.TreeLeft)]
    [InlineData("ctrl+alt+right", InputKind.TreeRight)]
    [InlineData("ctrl+c", InputKind.Copy)]
    [InlineData("ctrl+x", InputKind.Cut)]
    [InlineData("ctrl+v", InputKind.Paste)]
    [InlineData("ctrl+a", InputKind.SelectAll)]
    [InlineData("ctrl+z", InputKind.Undo)]
    [InlineData("ctrl+shift+z", InputKind.Redo)]
    [InlineData("alt+e", InputKind.Shortcut)]
    [InlineData("alt+up", InputKind.RawKey)]
    [InlineData("ctrl+backspace", InputKind.RawKey)]
    [InlineData("win+left", InputKind.RawKey)]
    [InlineData("left", InputKind.MoveLeft)]
    [InlineData("tab", InputKind.NavigateNext)]
    public void MacEditingKeys(string combo, InputKind expected) =>
        Assert.Equal(expected, Kind(combo, KeyboardConvention.Mac));

    [Theory]
    [InlineData("alt+left", InputKind.TreeLeft)]
    [InlineData("alt+up", InputKind.TreeUp)]
    [InlineData("ctrl+left", InputKind.MoveWordLeft)]
    [InlineData("ctrl+up", InputKind.RawKey)]
    [InlineData("ctrl+backspace", InputKind.DeleteWordBackward)]
    [InlineData("ctrl+alt+up", InputKind.RawKey)]
    [InlineData("ctrl+c", InputKind.Copy)]
    public void WindowsKeysUnchanged(string combo, InputKind expected)
    {
        Assert.Equal(expected, Kind(combo, KeyboardConvention.Windows));
        Assert.Equal(expected, InputMapper.MapCombo(ComboSpec.Parse(combo))?.Kind);
    }

    [Fact]
    public void MappedInputsCarryTheirCombo()
    {
        var input = InputMapper.MapCombo(ComboSpec.Parse("alt+left"), KeyboardConvention.Mac);
        Assert.Equal(ComboSpec.Parse("alt+left"), KeyCombo.FromInput(input!.Value));
    }

    // ── Physical keys ──

    [Fact]
    public void CommandIsCtrlAndControlIsWinUnderMac()
    {
        Assert.Equal((Key.Char('s').Code, Mods.Ctrl),
            InputMapper.PhysicalCombo('s', Sdl3.KmodLGui, KeyboardConvention.Mac));
        Assert.Equal((Key.Char('s').Code, Mods.Win),
            InputMapper.PhysicalCombo('s', Sdl3.KmodLCtrl, KeyboardConvention.Mac));
        Assert.Equal((Key.Ctrl.Code, Mods.Ctrl),
            InputMapper.PhysicalCombo(Sdl3.KeyLGui, Sdl3.KmodLGui, KeyboardConvention.Mac));
    }

    [Fact]
    public void ModifiersAreThemselvesUnderWindows()
    {
        Assert.Equal((Key.Char('s').Code, Mods.Win), InputMapper.PhysicalCombo('s', Sdl3.KmodLGui));
        Assert.Equal((Key.Char('s').Code, Mods.Ctrl), InputMapper.PhysicalCombo('s', Sdl3.KmodLCtrl));
        Assert.Equal((Key.Win.Code, Mods.Win), InputMapper.PhysicalCombo(Sdl3.KeyLGui, Sdl3.KmodLGui));
    }

    // ── Spoken names ──

    [Theory]
    [InlineData("ctrl+s", "command s", "control s")]
    [InlineData("ctrl+shift+z", "shift command z", "control shift z")]
    [InlineData("alt+f4", "option f4", "alt f4")]
    [InlineData("win+ctrl+alt+shift+k", "control option shift command k", "windows control alt shift k")]
    public void ComboNamesFollowTheConvention(string combo, string mac, string windows)
    {
        var parsed = ComboSpec.Parse(combo);
        Assert.Equal(mac, parsed.DisplayName(KeyboardConvention.Mac));
        Assert.Equal(windows, parsed.DisplayName(KeyboardConvention.Windows));
        Assert.Equal(windows, parsed.DisplayName());
    }

    // ── Reservations ──

    [Fact]
    public void MacReservesOptionLettersAndCommandOptionArrows()
    {
        var mac = KeyboardConvention.Mac;
        Assert.Equal("Option plus a letter is reserved for widget shortcuts",
            ComboSpec.Parse("alt+e").ReservedReasonUnder(mac));
        Assert.Equal("Command Option plus arrows is reserved for tree navigation",
            ComboSpec.Parse("ctrl+alt+left").ReservedReasonUnder(mac));
        Assert.Null(ComboSpec.Parse("alt+left").ReservedReasonUnder(mac));
        Assert.Equal("Tab is reserved for moving between widgets",
            ComboSpec.Parse("tab").ReservedReasonUnder(mac));
    }

    [Fact]
    public void WindowsReservationsUnchanged()
    {
        Assert.Equal("Alt plus arrows is reserved for tree navigation",
            ComboSpec.Parse("alt+left").ReservedReason);
        Assert.Equal("Alt plus a letter is reserved for widget shortcuts",
            ComboSpec.Parse("alt+e").ReservedReason);
        Assert.Null(ComboSpec.Parse("ctrl+alt+left").ReservedReason);
    }

    [Fact]
    public void AppReservesUnderItsConvention()
    {
        var app = SruiApp.Headless();
        Assert.Equal("Alt plus a letter is reserved for widget shortcuts",
            app.ReservedReasonFor(ComboSpec.Parse("alt+e")));
        app.KeyboardConvention = KeyboardConvention.Mac;
        Assert.Equal("Option plus a letter is reserved for widget shortcuts",
            app.ReservedReasonFor(ComboSpec.Parse("alt+e")));
    }

    // ── End to end ──

    [Fact]
    public void MacEditingMovesTheCursor()
    {
        using var ui = new TestApp();
        ui.App.KeyboardConvention = KeyboardConvention.Mac;
        var edit = new EditBox(ui.App, "Name", "");
        edit.Focus();
        edit.Text = "hello world";
        edit.CursorPosition = edit.Length;

        ui.Press("alt+left");
        Assert.Equal(6, edit.CursorPosition);
        ui.Press("ctrl+left");
        Assert.Equal(0, edit.CursorPosition);
        ui.Press("ctrl+right");
        Assert.Equal(11, edit.CursorPosition);
        ui.Press("alt+backspace");
        Assert.Equal("hello ", edit.Text);
        ui.Press("ctrl+z");
        Assert.Equal("hello world", edit.Text);
    }

    [Fact]
    public void ShortcutsAreSpokenByTheirMacNames()
    {
        using var ui = new TestApp();
        ui.App.KeyboardConvention = KeyboardConvention.Mac;
        var field = new ShortcutField(ui.App, "Shortcut");
        field.Combo = ComboSpec.Parse("ctrl+shift+s");
        Assert.Equal("shift command s", field.Value);
    }

    [Fact]
    public void HostedAppsTakeTheHostsConvention()
    {
        var host = MultiAppHost.Headless();
        host.KeyboardConvention = KeyboardConvention.Mac;
        var hosted = host.Add("Notes");
        Assert.Equal(KeyboardConvention.Mac, hosted.App.KeyboardConvention);
        Assert.Equal("command tab is reserved for switching apps",
            hosted.App.ReservedReasonFor(ComboSpec.Parse("ctrl+tab")));
    }
}
