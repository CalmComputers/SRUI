using Srui;
using Xunit;

namespace Srui.Net.Tests;

/// <summary>Item operations and the IListItem contract: structural
/// consequences (selection clamping, what the user hears about where the
/// selection landed) belong to the framework; editorial feedback
/// ("Deleted X.") belongs to the caller.</summary>
public class ListItemTests
{
    /// <summary>An item whose spoken line is computed from mutable
    /// state — the live-read consumer.</summary>
    private sealed class Chore(string title) : IListItem
    {
        public bool Done { get; set; }

        public string Text => Done ? $"{title}, done" : title;
    }

    /// <summary>A command-palette item: it ranks itself against the
    /// query, and "hidden" opts out of matching entirely.</summary>
    private sealed class Command(string name, int rank) : IListItem
    {
        public string Text => name;

        public int? FilterScore(string query) => name == "hidden" ? null : rank;
    }

    private static (TestUi Ui, ListBox List) FocusedList(params string[] items)
    {
        var ui = new TestUi();
        var list = new ListBox(ui.App, "Tasks", items, numbered: true);
        list.Focus();
        ui.Drain();
        return (ui, list);
    }

    [Fact]
    public void RemoveAtSelectedSpeaksTheSurvivor()
    {
        var (ui, list) = FocusedList("a", "b", "c");
        ui.Input(InputKind.MoveDown); // b
        ui.Drain();

        list.RemoveAt(1);
        Assert.Equal(new[] { "c 2 of 2" }, ui.Spoken());
        Assert.Equal(1, list.SelectedIndex);
    }

    [Fact]
    public void RemoveAtLastSelectedClampsAndSpeaks()
    {
        var (ui, list) = FocusedList("a", "b");
        ui.Input(InputKind.MoveDown); // b
        ui.Drain();

        list.RemoveAt(1);
        Assert.Equal(new[] { "a 1 of 1" }, ui.Spoken());
        Assert.Equal(0, list.SelectedIndex);
    }

    [Fact]
    public void RemovingTheLastItemSaysEmpty()
    {
        var (ui, list) = FocusedList("only");
        list.RemoveAt(0);
        Assert.Equal(new[] { "empty" }, ui.Spoken());
        Assert.Equal(-1, list.SelectedIndex);
        Assert.Null(list.SelectedItem);
    }

    [Fact]
    public void RemoveAtElsewhereIsSilentAndKeepsTheItem()
    {
        var (ui, list) = FocusedList("a", "b", "c");
        ui.Input(InputKind.MoveDown); // b
        ui.Drain();

        list.RemoveAt(0);
        Assert.Empty(ui.Spoken());
        Assert.Equal(0, list.SelectedIndex);
        Assert.Equal("b", list.SelectedItem?.Text);
    }

    [Fact]
    public void RemoveAtUnfocusedIsSilent()
    {
        var ui = new TestUi();
        var list = new ListBox(ui.App, "Tasks", ["a", "b"]);
        var other = new Button(ui.App, "Other");
        other.Focus();
        ui.Drain();

        list.RemoveAt(0);
        Assert.Empty(ui.Spoken());
        Assert.Equal("b", list.SelectedItem?.Text);
    }

    [Fact]
    public void InsertIsSilentAndKeepsTheSelectedItem()
    {
        var (ui, list) = FocusedList("a", "b");
        ui.Input(InputKind.MoveDown); // b
        ui.Drain();

        list.Insert(0, "start");
        Assert.Empty(ui.Spoken());
        Assert.Equal(2, list.SelectedIndex);
        Assert.Equal("b", list.SelectedItem?.Text);

        list.Add("end");
        Assert.Empty(ui.Spoken());
        Assert.Equal(4, list.Items.Count);
    }

    [Fact]
    public void InsertIntoEmptyFocusedListSpeaksTheItem()
    {
        var ui = new TestUi();
        var list = new ListBox(ui.App, "Tasks", Array.Empty<string>(), numbered: true);
        list.Focus();
        ui.Drain();

        list.Add("water");
        Assert.Equal(new[] { "water 1 of 1" }, ui.Spoken());
        Assert.Equal(0, list.SelectedIndex);

        // Only the transition out of empty speaks; a further Add is silent.
        list.Add("feed");
        Assert.Empty(ui.Spoken());
    }

    [Fact]
    public void InsertIntoEmptyUnfocusedListIsSilent()
    {
        var ui = new TestUi();
        var list = new ListBox(ui.App, "Tasks", Array.Empty<string>());
        var other = new Button(ui.App, "Other");
        other.Focus();
        ui.Drain();

        list.Add("water");
        Assert.Empty(ui.Spoken());
    }

    [Fact]
    public void SetItemIsSilentAndFocusAnnouncementsStayCurrent()
    {
        var (ui, list) = FocusedList("a", "b");
        list.SetItem(0, "alpha");
        Assert.Empty(ui.Spoken());

        ui.Input(InputKind.SpeakFocus);
        Assert.Contains("alpha", Assert.Single(ui.Spoken()));
    }

    [Fact]
    public void MutatedItemLinesAreReadLiveAtAnnouncement()
    {
        var ui = new TestUi();
        var chore = new Chore("sweep");
        var list = new ListBox(ui.App, "Chores", [chore]);
        list.Focus();
        ui.Drain();

        // No sync call of any kind: the label is pulled fresh when the
        // announcement is composed.
        chore.Done = true;
        ui.Input(InputKind.SpeakFocus);
        Assert.Contains("sweep, done", Assert.Single(ui.Spoken()));
    }

    [Fact]
    public void MutatedItemLinesAreLiveAcrossFocusMoves()
    {
        var ui = new TestUi();
        var chore = new Chore("sweep");
        var list = new ListBox(ui.App, "Chores", [chore]);
        var other = new Button(ui.App, "Other");
        list.Focus();
        ui.Drain();

        chore.Done = true;
        other.Focus();
        ui.Drain();
        list.Focus();
        Assert.Contains("sweep, done", Assert.Single(ui.Spoken()));
    }

    [Fact]
    public void TypeaheadMatchesTheItemText()
    {
        var ui = new TestUi();
        var done = new Chore("sweep") { Done = true };
        var list = new ListBox(ui.App, "Chores", [new Chore("dust"), done]);
        list.Focus();
        ui.Drain();

        ui.App.SetNow(100);
        ui.Type('s');
        Assert.Same(done, list.SelectedItem);
    }

    [Fact]
    public void FilterScoreDrivesMatchingAndRanking()
    {
        var ui = new TestUi();
        var list = new FilterListBox(ui.App, "Palette",
            [new Command("open file", 1), new Command("open recent", 5), new Command("hidden", 9)]);
        list.Focus();
        ui.Drain();

        ui.Type('o');
        // "hidden" excludes itself (null); "open recent" outranks by score.
        Assert.Equal(new[] { "open recent", "open file" },
            list.Results.Select(r => r.Text));
        Assert.Equal(new[] { "open recent 1 of 2" }, ui.Spoken());
    }

    [Fact]
    public void EmptyQueryBypassesScoresAndKeepsListOrder()
    {
        var ui = new TestUi();
        var list = new FilterListBox(ui.App, "Palette",
            [new Command("beta", 1), new Command("alpha", 5)]);

        Assert.Equal(new[] { "beta", "alpha" }, list.Results.Select(r => r.Text));
    }

    [Fact]
    public void TypedListBoxReadsItemsBackWithoutCasts()
    {
        var ui = new TestUi();
        var sweep = new Chore("sweep");
        var dust = new Chore("dust");
        var list = new ListBox<Chore>(ui.App, "Chores", [sweep, dust]);
        list.Focus();
        ui.Drain();

        Chore? selected = list.SelectedItem; // typed — no cast
        Assert.Same(sweep, selected);

        // The typed surface supports LINQ over item state directly.
        list.SetItems(list.Items.Where(c => !ReferenceEquals(c, sweep)).ToList());
        ui.Drain();
        Assert.Same(dust, list.SelectedItem);
        Assert.All(list.Items, c => Assert.False(c.Done));
    }

    [Fact]
    public void TypedFilterListBoxReturnsTypedResults()
    {
        var ui = new TestUi();
        var open = new Command("open file", 1);
        var list = new FilterListBox<Command>(ui.App, "Palette",
            [open, new Command("hidden", 9)]);
        list.Focus();
        ui.Drain();

        ui.Type('o');
        ui.Drain();
        Command? selected = list.SelectedItem; // typed — no cast
        Assert.Same(open, selected);
        Assert.Equal([open], list.Results);
    }

    [Fact]
    public void FilterWithNoResultsAnswersArrowsWithEmpty()
    {
        var ui = new TestUi();
        var list = new FilterListBox(ui.App, "Palette", ["alpha"]);
        list.Focus();
        ui.Drain();

        ui.Type('z');
        ui.Drain(); // "no results"

        Assert.True(ui.Input(InputKind.MoveDown));
        Assert.Equal(new[] { "empty" }, ui.Spoken());
    }
}
