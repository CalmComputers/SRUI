using Srui;
using Xunit;

namespace Srui.Tests;

/// <summary>Multi-select lists: the "multi select list" reading,
/// checked items speaking "checked" (and unchecked ones nothing) as
/// the cursor lands, the Enter and Space toggle modes, the checked
/// state living on the item, and checks surviving the item operations.</summary>
public class MultiSelectListTests
{
    private static (TestApp Ui, ListBox List) FocusedList(
        bool toggleWithSpace = false, bool numbered = false, params string[] items)
    {
        var ui = new TestApp();
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
        var ui = new TestApp();
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
    public void LandingOnACheckedItemFromACheckedItemStillSaysChecked()
    {
        var (ui, list) = FocusedList();
        list.SetChecked(0, true);
        list.SetChecked(1, true);
        ui.Spoken();

        // The value did not change between the two items; the landing
        // reads the item in full regardless.
        ui.Input(InputKind.MoveDown);
        Assert.Equal(new[] { "banana checked" }, ui.Spoken());
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
        var toggles = new List<(string? Text, bool Checked)>();
        list.ItemToggled += (item, isChecked) => toggles.Add((item.Value, isChecked));

        ui.Input(InputKind.Activate);
        ui.Input(InputKind.Activate);
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
        var ui = new TestApp();
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
        Assert.Equal("red berry", list.SelectedItem?.Value);
        Assert.False(list.IsChecked(1));
    }

    [Fact]
    public void SetCheckedReadsOnlyForTheFocusedSelection()
    {
        var (ui, list) = FocusedList();
        ui.Spoken();

        // Unselected item: not under the cursor, nothing to hear.
        list.SetChecked(2, true);
        Assert.Empty(ui.Spoken());

        // The selected item while focused: the state changed under
        // the cursor.
        list.SetChecked(0, true);
        Assert.Equal(new[] { "checked" }, ui.Spoken());

        // No change: silent.
        list.SetChecked(0, true);
        Assert.Empty(ui.Spoken());

        Assert.Equal(new[] { "apple", "cherry" }, list.CheckedItems.Select(i => i.Value));
    }

    [Fact]
    public void SuppressKeepsAProgramsCheckOutOfTheReading()
    {
        var (ui, list) = FocusedList();
        ui.Spoken();

        // A bulk sweep the caller narrates itself: the field changes,
        // the tick end leaves it out.
        list.Suppress(Fields.Checked);
        list.SetChecked(0, true);
        Assert.Empty(ui.Spoken());
        Assert.True(list.IsChecked(0));

        // The suppression lasted one tick.
        list.SetChecked(0, false);
        Assert.Equal(new[] { "not checked" }, ui.Spoken());
    }

    [Fact]
    public void SetCheckedSilentWhenUnfocused()
    {
        var ui = new TestApp();
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
    public void ChecksLiveOnTheItems()
    {
        var (ui, list) = FocusedList();
        list.SetChecked(0, true);
        list.SetChecked(1, true);
        ui.Spoken();

        // A removed item takes its check with it; the others keep theirs.
        list.RemoveAt(0);
        Assert.Equal(new[] { "banana" }, list.CheckedItems.Select(i => i.Value));
        Assert.True(list.IsChecked(0));

        // A replacement item arrives unchecked.
        list.SetItem(0, "blueberry");
        Assert.Empty(list.CheckedItems);
    }

    [Fact]
    public void ReplacingTheItemsKeepsChecksOnSurvivingItemObjects()
    {
        var ui = new TestApp();
        var a = new ListItem("a");
        var b = new ListItem("b");
        var list = new ListBox(ui.App, "L", new ListItem[] { a, b }, multiSelect: true);
        list.SetChecked(0, true);
        list.SetChecked(1, true);

        list.Items = new ListItem[] { b, new ListItem("c") };
        Assert.Equal(new[] { "b" }, list.CheckedItems.Select(i => i.Value));
    }

    [Fact]
    public void SingleSelectListHasNoToggleButItemsKeepTheirField()
    {
        var ui = new TestApp();
        var list = new ListBox(ui.App, "L", ["a"]);
        list.Focus();
        ui.Drain();
        Assert.False(list.MultiSelect);
        Assert.False(list.IsChecked(0));
        Assert.Empty(list.CheckedItems);

        // Enter is not a toggle here.
        Assert.False(ui.Input(InputKind.Activate));
        Assert.False(list.IsChecked(0));

        // The item's field is the item's, and a program may still set
        // it; the reading is honest about it.
        list.SetChecked(0, true);
        Assert.Equal(new[] { "checked" }, ui.Spoken());
    }

    private sealed class LockingListBox : ListBox
    {
        public LockingListBox(SruiApp app)
            : base(app, "L", new[] { "free", "locked" }, multiSelect: true) { }

        protected override bool CanToggle(ListItem item)
        {
            if (item.Value != "locked")
                return true;
            Announce("item is locked");
            return false;
        }
    }

    [Fact]
    public void CanToggleRefusesUserTogglesButNotSetChecked()
    {
        var ui = new TestApp();
        var list = new LockingListBox(ui.App);
        var toggles = 0;
        list.ItemToggled += (_, _) => toggles++;
        list.Focus();
        ui.Drain();
        ui.Spoken();

        // Allowed item toggles normally.
        ui.Input(InputKind.Activate);
        Assert.Equal(new[] { "checked" }, ui.Spoken());
        Assert.Equal(1, toggles);

        // Refused item: no state change, no toggle event, only the
        // subclass's refusal announcement.
        ui.Input(InputKind.MoveDown);
        ui.Spoken();
        ui.Input(InputKind.Activate);
        Assert.Equal(new[] { "item is locked" }, ui.Spoken());
        Assert.False(list.IsChecked(1));
        Assert.Equal(1, toggles);

        // The program is never refused.
        list.SetChecked(1, true);
        Assert.True(list.IsChecked(1));
        Assert.Equal(new[] { "checked" }, ui.Spoken());
    }

    [Fact]
    public void InvalidConstructionThrows()
    {
        var ui = new TestApp();
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
        Assert.True(activated);
    }
}
