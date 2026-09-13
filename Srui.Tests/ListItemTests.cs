using Srui;
using Xunit;

namespace Srui.Tests;

/// <summary>Item operations and the item contract: an item is an
/// Element whose fields the reader speaks when the cursor lands on
/// it, and whose identity the cursor follows. Where the cursor lands
/// after a structural change is the tick end's to read; editorial
/// feedback ("Deleted X.") belongs to the caller.</summary>
public partial class ListItemTests
{
    /// <summary>An item whose line is computed from mutable state — the
    /// live-read consumer.</summary>
    private sealed partial class Chore(string title) : Element
    {
        public bool Done { get; set; }

        [Field] public string Value => Done ? $"{title}, done" : title;
    }

    /// <summary>A command-palette item carrying its own rank; the list's
    /// Score consults it, and "hidden" opts out of matching entirely.</summary>
    private sealed partial class Command(string name, int rank) : Element
    {
        [Field] public string Value => name;

        public int? Rank => name == "hidden" ? null : rank;
    }

    private static (TestApp Ui, ListBox List) FocusedList(params string[] items)
    {
        var ui = new TestApp();
        var list = new ListBox(ui.App, "Tasks", items, numbered: true);
        list.Focus();
        ui.Drain();
        return (ui, list);
    }

    [Fact]
    public void RemoveAtSelectedReadsTheSurvivor()
    {
        var (ui, list) = FocusedList("a", "b", "c");
        ui.Input(InputKind.MoveDown); // b
        ui.Drain();

        list.RemoveAt(1);
        Assert.Equal(new[] { "c 2 of 2" }, ui.Spoken());
        Assert.Equal(1, list.SelectedIndex);
    }

    [Fact]
    public void RemoveAtLastSelectedClampsAndReads()
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
    public void RemoveAtElsewhereKeepsTheItemAndReadsThePosition()
    {
        var (ui, list) = FocusedList("a", "b", "c");
        ui.Input(InputKind.MoveDown); // b
        ui.Drain();

        // The cursor's item survives; only its place changed, and that
        // is what the reading carries.
        list.RemoveAt(0);
        Assert.Equal(new[] { "1 of 2" }, ui.Spoken());
        Assert.Equal(0, list.SelectedIndex);
        Assert.Equal("b", list.SelectedItem?.Value);
    }

    [Fact]
    public void RemoveAtUnfocusedIsSilent()
    {
        var ui = new TestApp();
        var list = new ListBox(ui.App, "Tasks", ["a", "b"]);
        var other = new Button(ui.App, "Other");
        other.Focus();
        ui.Drain();

        list.RemoveAt(0);
        Assert.Empty(ui.Spoken());
        Assert.Equal("b", list.SelectedItem?.Value);
    }

    [Fact]
    public void InsertKeepsTheSelectedItem()
    {
        var (ui, list) = FocusedList("a", "b");
        ui.Input(InputKind.MoveDown); // b
        ui.Drain();

        list.Insert(0, "start");
        Assert.Equal(new[] { "3 of 3" }, ui.Spoken());
        Assert.Equal(2, list.SelectedIndex);
        Assert.Equal("b", list.SelectedItem?.Value);

        list.Add("end");
        Assert.Equal(new[] { "3 of 4" }, ui.Spoken());
        Assert.Equal(4, list.Items.Count);
    }

    [Fact]
    public void InsertIntoEmptyFocusedListReadsTheItem()
    {
        var ui = new TestApp();
        var list = new ListBox(ui.App, "Tasks", Array.Empty<string>(), numbered: true);
        list.Focus();
        ui.Drain();

        list.Add("water");
        Assert.Equal(new[] { "water 1 of 1" }, ui.Spoken());
        Assert.Equal(0, list.SelectedIndex);

        // A further Add changes only the count the position carries.
        list.Add("feed");
        Assert.Equal(new[] { "1 of 2" }, ui.Spoken());
    }

    [Fact]
    public void InsertIntoEmptyUnfocusedListIsSilent()
    {
        var ui = new TestApp();
        var list = new ListBox(ui.App, "Tasks", Array.Empty<string>());
        var other = new Button(ui.App, "Other");
        other.Focus();
        ui.Drain();

        list.Add("water");
        Assert.Empty(ui.Spoken());
    }

    [Fact]
    public void SetItemOnTheCursorReadsTheReplacement()
    {
        var (ui, list) = FocusedList("a", "b");
        list.SetItem(0, "alpha");
        Assert.Equal(new[] { "alpha 1 of 2" }, ui.Spoken());

        // Elsewhere: nothing to hear.
        list.SetItem(1, "beta");
        Assert.Empty(ui.Spoken());
    }

    [Fact]
    public void MutatedItemLinesAreReadLive()
    {
        var ui = new TestApp();
        var chore = new Chore("sweep");
        var list = new ListBox<Chore>(ui.App, "Chores", [chore]);
        list.Focus();
        ui.Drain();

        // No sync call of any kind: the line changed under the cursor,
        // and the tick end reads the change.
        chore.Done = true;
        Assert.Equal(new[] { "sweep, done" }, ui.Spoken());
    }

    [Fact]
    public void MutatedItemLinesAreLiveAcrossFocusMoves()
    {
        var ui = new TestApp();
        var chore = new Chore("sweep");
        var list = new ListBox<Chore>(ui.App, "Chores", [chore]);
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
    public void TypeaheadMatchesTheItemLine()
    {
        var ui = new TestApp();
        var done = new Chore("sweep") { Done = true };
        var list = new ListBox<Chore>(ui.App, "Chores", [new Chore("dust"), done]);
        list.Focus();
        ui.Drain();

        ui.App.SetNow(100);
        ui.Type('s');
        Assert.Same(done, list.SelectedItem);
    }

    [Fact]
    public void ScoreDrivesMatchingAndRanking()
    {
        var ui = new TestApp();
        var list = new FilterListBox<Command>(ui.App, "Palette",
            [new Command("open file", 1), new Command("open recent", 5), new Command("hidden", 9)])
        {
            Score = static (item, _) => item.Rank,
        };
        list.Focus();

        ui.Type('o');
        // "hidden" excludes itself (null); "open recent" outranks by score.
        Assert.Equal(new[] { "open recent", "open file" },
            list.Results.Select(r => r.Value));
        Assert.Equal(new[] { "open recent 1 of 2" }, ui.Spoken());
    }

    [Fact]
    public void EmptyQueryBypassesScoresAndKeepsListOrder()
    {
        var ui = new TestApp();
        var list = new FilterListBox<Command>(ui.App, "Palette",
            [new Command("beta", 1), new Command("alpha", 5)])
        {
            Score = static (item, _) => item.Rank,
        };

        Assert.Equal(new[] { "beta", "alpha" }, list.Results.Select(r => r.Value));
    }

    [Fact]
    public void TypedListBoxReadsItemsBackWithoutCasts()
    {
        var ui = new TestApp();
        var sweep = new Chore("sweep");
        var dust = new Chore("dust");
        var list = new ListBox<Chore>(ui.App, "Chores", [sweep, dust]);
        list.Focus();
        ui.Drain();

        Chore? selected = list.SelectedItem; // typed — no cast
        Assert.Same(sweep, selected);

        // The typed surface supports LINQ over item state directly.
        list.Items = list.Items.Where(c => !ReferenceEquals(c, sweep)).ToList();
        ui.Drain();
        Assert.Same(dust, list.SelectedItem);
        Assert.All(list.Items, c => Assert.False(c.Done));
    }

    [Fact]
    public void TypedFilterListBoxReturnsTypedResults()
    {
        var ui = new TestApp();
        var open = new Command("open file", 1);
        var list = new FilterListBox<Command>(ui.App, "Palette",
            [open, new Command("hidden", 9)])
        {
            Score = static (item, _) => item.Rank,
        };
        list.Focus();

        ui.Type('o');
        Command? selected = list.SelectedItem; // typed — no cast
        Assert.Same(open, selected);
        Assert.Equal([open], list.Results);
    }

    [Fact]
    public void FilterWithNoResultsAnswersArrowsWithNoResults()
    {
        var ui = new TestApp();
        var list = new FilterListBox(ui.App, "Palette", ["alpha"]);
        list.Focus();

        ui.Type('z');

        Assert.True(ui.Input(InputKind.MoveDown));
        Assert.Equal(new[] { "no results" }, ui.Spoken());
    }

    [Fact]
    public void BoundItemsFollowTheModelWithNoCallToTheList()
    {
        var ui = new TestApp();
        var model = new List<Chore> { new("sweep"), new("dust"), new("mop") };
        var list = new ListBox<Chore>(ui.App, "Chores", [], numbered: true);
        list.BindItems(() => model);
        list.Focus();
        ui.Input(InputKind.MoveDown); // dust
        ui.Drain();

        // The model changes; the list is never told. The cursor keeps
        // its item through a reorder...
        var dust = model[1];
        model.RemoveAt(1);
        model.Add(dust);
        Assert.Equal(new[] { "3 of 3" }, ui.Spoken());
        Assert.Equal("dust", list.SelectedItem?.Value);

        // ...and lands on the survivor at its place when its item goes.
        model.Remove(dust);
        Assert.Equal(new[] { "mop 2 of 2" }, ui.Spoken());

        // Structural calls belong to the model now.
        Assert.Throws<InvalidOperationException>(() => list.RemoveAt(0));
    }
}
