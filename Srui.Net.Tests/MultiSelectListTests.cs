using Srui;
using Xunit;

namespace Srui.Net.Tests;

/// <summary>Multi-select lists: the "multi select list" role, checked
/// items speaking "checked" (and unchecked ones nothing), the Enter and
/// Space toggle modes, the programmatic checked surface, and checked
/// state surviving the item operations.</summary>
public class MultiSelectListTests
{
    private static (TestUi Ui, ListBox List) FocusedList(
        bool toggleWithSpace = false, bool numbered = false, params string[] items)
    {
        var ui = new TestUi();
        var list = new ListBox(
            ui.App, "Fruits", items.Length > 0 ? items : ["apple", "banana", "cherry"],
            numbered: numbered, multiSelect: true, toggleWithSpace: toggleWithSpace);
        list.Focus();
        ui.Drain();
        return (ui, list);
    }

    [Fact]
    public void AnnouncesAsMultiSelectList()
    {
        var ui = new TestUi();
        var list = new ListBox(ui.App, "Fruits", ["apple"], multiSelect: true);
        list.Focus();
        Assert.Equal(new[] { "Fruits multi select list apple" }, ui.Spoken());
    }

    [Fact]
    public void EnterTogglesAndSpeaksTheNewState()
    {
        var (ui, list) = FocusedList();
        ui.Spoken();

        Assert.True(ui.Input(InputKind.Activate));
        Assert.Equal(new[] { "checked" }, ui.Spoken());
        Assert.True(list.IsChecked(0));

        Assert.True(ui.Input(InputKind.Activate));
        Assert.Equal(new[] { "not checked" }, ui.Spoken());
        Assert.False(list.IsChecked(0));
    }

    [Fact]
    public void NavigationSpeaksCheckedOnCheckedItemsOnly()
    {
        var (ui, _) = FocusedList();
        ui.Input(InputKind.Activate); // check apple
        ui.Spoken();

        ui.Input(InputKind.MoveDown);
        Assert.Equal(new[] { "banana" }, ui.Spoken());

        ui.Input(InputKind.MoveUp);
        Assert.Equal(new[] { "apple checked" }, ui.Spoken());
    }

    [Fact]
    public void CheckedRidesBeforePositionWhenNumbered()
    {
        var (ui, list) = FocusedList(numbered: true);
        ui.Input(InputKind.Activate);
        ui.Input(InputKind.MoveDown);
        ui.Spoken();

        ui.Input(InputKind.MoveUp);
        Assert.Equal(new[] { "apple checked 1 of 3" }, ui.Spoken());
        Assert.True(list.IsChecked(0));
    }

    [Fact]
    public void FocusAnnouncementCarriesCheckedState()
    {
        var (ui, list) = FocusedList();
        ui.Input(InputKind.Activate);
        ui.Drain();
        ui.Spoken();

        var other = new Button(ui.App, "Other");
        other.Focus();
        ui.Drain();
        ui.Spoken();
        list.Focus();
        Assert.Equal(new[] { "Fruits multi select list apple checked" }, ui.Spoken());
    }

    [Fact]
    public void ItemToggledReportsItemAndState()
    {
        var (ui, list) = FocusedList();
        var toggles = new List<(string Text, bool Checked)>();
        list.ItemToggled += (item, isChecked) => toggles.Add((item.Text, isChecked));

        ui.Input(InputKind.Activate);
        ui.Input(InputKind.Activate);
        ui.Drain();
        Assert.Equal([("apple", true), ("apple", false)], toggles);
    }

    [Fact]
    public void SpaceModeTogglesWithSpaceAndLeavesEnterAlone()
    {
        var (ui, list) = FocusedList(toggleWithSpace: true);
        ui.Spoken();

        Assert.True(ui.Type(' '));
        Assert.Equal(new[] { "checked" }, ui.Spoken());
        Assert.True(list.IsChecked(0));

        // Enter is unclaimed: it falls through to the layer's primary.
        Assert.False(ui.Input(InputKind.Activate));
        Assert.Empty(ui.Spoken());
    }

    [Fact]
    public void EnterModeKeepsSpaceForTypeahead()
    {
        var ui = new TestUi();
        var list = new ListBox(
            ui.App, "Files", ["red apple", "red berry"], multiSelect: true);
        list.Focus();
        ui.Drain();
        ui.Spoken();

        // Multi-word prefix search still works: "red b" lands on red berry.
        ui.App.SetNow(1000);
        ui.Type('r');
        ui.Type('e');
        ui.Type('d');
        ui.Type(' ');
        ui.Type('b');
        Assert.Equal("red berry", list.SelectedItem?.Text);
        Assert.False(list.IsChecked(1));
    }

    [Fact]
    public void SetCheckedSpeaksOnlyForTheFocusedSelection()
    {
        var (ui, list) = FocusedList();
        ui.Spoken();

        // Unselected item: silent.
        list.SetChecked(2, true);
        Assert.Empty(ui.Spoken());

        // The selected item while focused: speaks like a user toggle.
        list.SetChecked(0, true);
        Assert.Equal(new[] { "checked" }, ui.Spoken());

        // No change: silent.
        list.SetChecked(0, true);
        Assert.Empty(ui.Spoken());

        Assert.Equal(new[] { "apple", "cherry" }, list.CheckedItems.Select(i => i.Text));
    }

    [Fact]
    public void SetCheckedSilentWhenUnfocused()
    {
        var ui = new TestUi();
        var list = new ListBox(ui.App, "Fruits", ["a", "b"], multiSelect: true);
        var other = new Button(ui.App, "Other");
        other.Focus();
        ui.Drain();
        ui.Spoken();

        list.SetChecked(0, true);
        Assert.Empty(ui.Spoken());
        Assert.True(list.IsChecked(0));
    }

    [Fact]
    public void ItemOperationsDropOrphanedChecks()
    {
        var (ui, list) = FocusedList();
        list.SetChecked(0, true);
        list.SetChecked(1, true);
        ui.Spoken();

        // Removing a checked item forgets its check; the others keep theirs.
        list.RemoveAt(0);
        Assert.Equal(new[] { "banana" }, list.CheckedItems.Select(i => i.Text));
        Assert.True(list.IsChecked(0));

        // Replacing an item drops the old item's check.
        list.SetItem(0, "blueberry");
        Assert.Empty(list.CheckedItems);
    }

    [Fact]
    public void SetItemsKeepsChecksOnSurvivingItemObjects()
    {
        var ui = new TestUi();
        var a = new ListItem("a");
        var b = new ListItem("b");
        var list = new ListBox(ui.App, "L", new IListItem[] { a, b }, multiSelect: true);
        list.SetChecked(0, true);
        list.SetChecked(1, true);

        list.SetItems(new IListItem[] { b, new ListItem("c") });
        Assert.Equal(new[] { "b" }, list.CheckedItems.Select(i => i.Text));
    }

    [Fact]
    public void SingleSelectSurfaceStaysInert()
    {
        var ui = new TestUi();
        var list = new ListBox(ui.App, "L", ["a"]);
        Assert.False(list.MultiSelect);
        Assert.False(list.IsChecked(0));
        Assert.Empty(list.CheckedItems);
        Assert.Throws<InvalidOperationException>(() => list.SetChecked(0, true));
    }

    [Fact]
    public void InvalidConstructionThrows()
    {
        var ui = new TestUi();
        Assert.Throws<ArgumentException>(() =>
            new ListBox(ui.App, "L", ["a"], toggleWithSpace: true));
        Assert.Throws<ArgumentException>(() =>
            new ListBox(ui.App, "L", ["a"], activateItems: true, multiSelect: true));
        // Space mode frees Enter, so activateItems composes with it.
        var list = new ListBox(
            ui.App, "L", ["a"], activateItems: true, multiSelect: true, toggleWithSpace: true);
        list.Focus();
        ui.Drain();
        ui.Spoken();
        var activated = false;
        list.Activated += () => activated = true;
        ui.Input(InputKind.Activate);
        ui.Drain();
        Assert.True(activated);
    }
}
