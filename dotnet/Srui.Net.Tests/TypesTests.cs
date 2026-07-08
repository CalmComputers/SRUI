using Srui;
using Srui.Core;
using Xunit;

namespace Srui.Net.Tests;

public class TypesTests
{
    [Fact]
    public void RoleDisplay()
    {
        Assert.Equal("button", Role.Button.ToSpeech());
        Assert.Equal("check box", Role.CheckBox.ToSpeech());
        Assert.Equal("edit", Role.Edit().ToSpeech());
        Assert.Equal("edit read only", Role.Edit(readOnly: true).ToSpeech());
        Assert.Equal("edit multi line", Role.Edit(multiline: true).ToSpeech());
        Assert.Equal("edit read only multi line", Role.Edit(true, true).ToSpeech());
        Assert.Equal("list", Role.ListBox.ToSpeech());
        Assert.Equal("group", Role.Group.ToSpeech());
        Assert.Equal("tab control", Role.TabControl.ToSpeech());
    }

    [Fact]
    public void FocusableRules()
    {
        Assert.True(WidgetLabel.IsFocusable(Role.Button, States.None));
        Assert.True(WidgetLabel.IsFocusable(Role.CheckBox, States.None));
        Assert.True(WidgetLabel.IsFocusable(Role.Edit(), States.None));
        Assert.True(WidgetLabel.IsFocusable(Role.ListBox, States.None));
        Assert.True(WidgetLabel.IsFocusable(Role.TabControl, States.None));
        Assert.False(WidgetLabel.IsFocusable(Role.Group, States.None));
        Assert.False(WidgetLabel.IsFocusable(Role.Label, States.None));
        Assert.False(WidgetLabel.IsFocusable(Role.Button, States.Disabled));
        Assert.False(WidgetLabel.IsFocusable(Role.Button, States.Hidden));
        Assert.False(WidgetLabel.IsFocusable(Role.CheckBox, States.Hidden));
    }

    [Fact]
    public void StatesBitflags()
    {
        var s = States.Focused | States.Disabled;
        Assert.True((s & States.Focused) != 0);
        Assert.True((s & States.Disabled) != 0);
        Assert.False((s & States.Required) != 0);
    }

    [Fact]
    public void ButtonReservesEnterAndSpace()
    {
        Assert.True(Role.Button.ReservesKey(KeyCombo.Plain(Key.Enter)));
        Assert.True(Role.Button.ReservesKey(KeyCombo.Plain(Key.Space)));
        Assert.False(Role.Button.ReservesKey(KeyCombo.WithCtrl(Key.Char('s'))));
        Assert.False(Role.Button.ReservesKey(KeyCombo.Plain(Key.Up)));
    }

    [Fact]
    public void EditboxReservesTypingAndNavigation()
    {
        var edit = Role.Edit();
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
        Assert.True(Role.ListBox.ReservesKey(KeyCombo.Plain(Key.Up)));
        Assert.True(Role.ListBox.ReservesKey(KeyCombo.Plain(Key.Down)));
        Assert.True(Role.ListBox.ReservesKey(KeyCombo.Plain(Key.Home)));
        Assert.True(Role.ListBox.ReservesKey(KeyCombo.Plain(Key.Enter)));
        Assert.True(Role.ListBox.ReservesKey(KeyCombo.Plain(Key.Char('a'))));
        Assert.True(Role.ListBox.ReservesKey(KeyCombo.Plain(Key.Backspace)));
        Assert.False(Role.ListBox.ReservesKey(KeyCombo.WithCtrl(Key.Char('s'))));
    }

    [Fact]
    public void TabControlReservesArrowsAndEdges()
    {
        Assert.True(Role.TabControl.ReservesKey(KeyCombo.Plain(Key.Left)));
        Assert.True(Role.TabControl.ReservesKey(KeyCombo.Plain(Key.Right)));
        Assert.True(Role.TabControl.ReservesKey(KeyCombo.Plain(Key.Home)));
        Assert.True(Role.TabControl.ReservesKey(KeyCombo.Plain(Key.Up)));
        Assert.True(Role.TabControl.ReservesKey(KeyCombo.Plain(Key.Down)));
        Assert.False(Role.TabControl.ReservesKey(KeyCombo.WithCtrl(Key.Char('s'))));
    }

    [Fact]
    public void GroupAndLabelReserveNothing()
    {
        var ctrlS = KeyCombo.WithCtrl(Key.Char('s'));
        Assert.False(Role.Group.ReservesKey(ctrlS));
        Assert.False(Role.Label.ReservesKey(ctrlS));
        Assert.False(Role.Group.ReservesKey(KeyCombo.Plain(Key.Enter)));
        Assert.False(Role.Label.ReservesKey(KeyCombo.Plain(Key.Enter)));
    }

    [Fact]
    public void MatchesKindIgnoresEditboxFlags()
    {
        var edit1 = Role.Edit();
        var edit2 = Role.Edit(true, true);
        Assert.True(edit1.MatchesKind(edit2));
        Assert.True(edit2.MatchesKind(edit1));
        Assert.False(Role.Button.MatchesKind(Role.CheckBox));
        Assert.True(Role.Button.MatchesKind(Role.Button));
    }
}
