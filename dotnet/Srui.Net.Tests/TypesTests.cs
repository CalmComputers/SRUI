using Srui;
using Xunit;

namespace Srui.Net.Tests;

/// <summary>Reservation tables — the soft-conflict side of bind-dialog
/// warnings, one virtual per widget kind.</summary>
public class ReservationTests
{
    private static readonly SruiApp App = SruiApp.Headless();

    [Fact]
    public void ButtonReservesEnterAndSpace()
    {
        var button = new Button(App, "Save");
        Assert.True(button.ReservesKey(KeyCombo.Plain(Key.Enter)));
        Assert.True(button.ReservesKey(KeyCombo.Plain(Key.Space)));
        Assert.False(button.ReservesKey(KeyCombo.WithCtrl(Key.Char('s'))));
        Assert.False(button.ReservesKey(KeyCombo.Plain(Key.Up)));
    }

    [Fact]
    public void EditboxReservesTypingAndNavigation()
    {
        var edit = new EditBox(App, "Notes");
        // Typing
        Assert.True(edit.ReservesKey(KeyCombo.Plain(Key.Char('a'))));
        Assert.True(edit.ReservesKey(KeyCombo.Plain(Key.Space)));
        Assert.True(edit.ReservesKey(KeyCombo.Plain(Key.Enter)));
        // Navigation
        Assert.True(edit.ReservesKey(KeyCombo.Plain(Key.Left)));
        Assert.True(edit.ReservesKey(KeyCombo.WithCtrl(Key.Left)));
        // Selection
        Assert.True(edit.ReservesKey(new KeyCombo(Key.Right, false, false, true)));
        Assert.True(edit.ReservesKey(new KeyCombo(Key.Right, true, false, true)));
        // Clipboard
        Assert.True(edit.ReservesKey(KeyCombo.WithCtrl(Key.Char('c'))));
        Assert.True(edit.ReservesKey(KeyCombo.WithCtrl(Key.Char('v'))));
        // Deletion
        Assert.True(edit.ReservesKey(KeyCombo.Plain(Key.Backspace)));
        Assert.True(edit.ReservesKey(KeyCombo.WithCtrl(Key.Backspace)));
        // Does NOT reserve arbitrary Ctrl combos
        Assert.False(edit.ReservesKey(KeyCombo.WithCtrl(Key.Char('s'))));
        Assert.False(edit.ReservesKey(KeyCombo.WithCtrl(Key.Char('n'))));
    }

    [Fact]
    public void ListboxReservesNavigationAndTypeahead()
    {
        var list = new ListBox(App, "Files", ["a"]);
        Assert.True(list.ReservesKey(KeyCombo.Plain(Key.Up)));
        Assert.True(list.ReservesKey(KeyCombo.Plain(Key.Down)));
        Assert.True(list.ReservesKey(KeyCombo.Plain(Key.Home)));
        Assert.True(list.ReservesKey(KeyCombo.Plain(Key.Enter)));
        Assert.True(list.ReservesKey(KeyCombo.Plain(Key.Char('a'))));
        Assert.True(list.ReservesKey(KeyCombo.Plain(Key.Backspace)));
        Assert.False(list.ReservesKey(KeyCombo.WithCtrl(Key.Char('s'))));
    }

    [Fact]
    public void TabControlReservesArrowsAndEdges()
    {
        var tabs = new TabControl(App, "Views", ["A", "B"]);
        Assert.True(tabs.ReservesKey(KeyCombo.Plain(Key.Left)));
        Assert.True(tabs.ReservesKey(KeyCombo.Plain(Key.Right)));
        Assert.True(tabs.ReservesKey(KeyCombo.Plain(Key.Home)));
        Assert.True(tabs.ReservesKey(KeyCombo.Plain(Key.Up)));
        Assert.True(tabs.ReservesKey(KeyCombo.Plain(Key.Down)));
        Assert.False(tabs.ReservesKey(KeyCombo.WithCtrl(Key.Char('s'))));
    }

    [Fact]
    public void ShortcutFieldReservesEverything()
    {
        var field = new ShortcutField(App, "Shortcut");
        Assert.True(field.ReservesKey(KeyCombo.WithCtrl(Key.Char('s'))));
        Assert.True(field.ReservesKey(KeyCombo.Plain(Key.F(5))));
    }

    [Fact]
    public void SliderReservesArrowsEdgesAndPaging()
    {
        var slider = new Slider(App, "Volume", 0, 0, 100);
        Assert.True(slider.ReservesKey(KeyCombo.Plain(Key.Left)));
        Assert.True(slider.ReservesKey(KeyCombo.WithShift(Key.Right)));
        Assert.True(slider.ReservesKey(KeyCombo.Plain(Key.PageUp)));
        Assert.True(slider.ReservesKey(KeyCombo.Plain(Key.Home)));
        Assert.False(slider.ReservesKey(KeyCombo.WithCtrl(Key.Char('s'))));
    }

    [Fact]
    public void PassiveWidgetsReserveNothing()
    {
        var ctrlS = KeyCombo.WithCtrl(Key.Char('s'));
        var group = new Group(App, "Options");
        var label = new Label(App, "Prompt");
        var custom = new CustomWidget(App, "Arena");
        Assert.False(group.ReservesKey(ctrlS));
        Assert.False(label.ReservesKey(ctrlS));
        Assert.False(custom.ReservesKey(ctrlS));
        Assert.False(group.ReservesKey(KeyCombo.Plain(Key.Enter)));
        Assert.False(custom.ReservesKey(KeyCombo.Plain(Key.Enter)));
    }
}

public class WidgetStatesTests
{
    [Fact]
    public void StatesBitflags()
    {
        var s = WidgetStates.Hidden | WidgetStates.Disabled;
        Assert.True((s & WidgetStates.Hidden) != 0);
        Assert.True((s & WidgetStates.Disabled) != 0);
        Assert.False((s & WidgetStates.Required) != 0);
    }
}
