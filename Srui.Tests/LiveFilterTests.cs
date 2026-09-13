using Srui;
using Xunit;

namespace Srui.Tests;

/// <summary>The live-source seams on FilterListBox: a bound pool that
/// re-queries an external source per read, and OnFilterChanged for a
/// stored pool swapped per filter — either way the tick end reads the
/// fresh results, never a stale set.</summary>
public partial class LiveFilterTests
{
    private sealed partial class Item : Element
    {
        public Item(string text, int score)
        {
            Value = text;
            Score = score;
        }

        [Field] public partial string? Value { get; set; }

        public int Score { get; }
    }

    /// <summary>A filter list whose pool is whatever the "engine"
    /// returns for the current filter, read live.</summary>
    private sealed class LiveList : FilterListBox<Item>
    {
        public Func<string, IReadOnlyList<Item>> Source { get; set; } =
            _ => Array.Empty<Item>();

        public LiveList(IWidgetContainer parent)
            : base(parent, "Search", Array.Empty<Item>())
        {
            Score = static (item, _) => item.Score;
            BindItems(() => Source(Filter ?? ""));
        }
    }

    /// <summary>The stored-pool form: every filter change swaps the
    /// whole item set through the OnFilterChanged seam.</summary>
    private sealed class SwappingList : FilterListBox<Item>
    {
        public Func<string, IReadOnlyList<Item>> Source { get; set; } =
            _ => Array.Empty<Item>();

        public SwappingList(IWidgetContainer parent)
            : base(parent, "Search", Array.Empty<Item>())
        {
            Score = static (item, _) => item.Score;
        }

        protected override void OnFilterChanged(string? filter) => Items = Source(filter ?? "");
    }

    [Fact]
    public void FilterReportReadsTheFreshlyQueriedItems()
    {
        using var ui = new TestApp();
        var list = new LiveList(ui.App)
        {
            Source = filter => filter.Length == 0
                ? Array.Empty<Item>()
                : new[] { new Item($"result for {filter}", 2), new Item("runner-up", 1) },
        };
        list.Focus();

        ui.Type('x');
        var spoken = ui.Spoken();
        Assert.Contains(spoken, s => s.Contains("result for x") && s.Contains("2"));

        Assert.Equal("result for x", list.SelectedItem?.Value);
    }

    [Fact]
    public void ErasingTheFilterQueriesAgain()
    {
        using var ui = new TestApp();
        var browse = new[] { new Item("browse entry", 1) };
        var list = new LiveList(ui.App)
        {
            Source = filter => filter.Length == 0
                ? browse
                : new[] { new Item("match", 1) },
        };
        list.Focus();
        ui.Type('x');

        ui.Input(InputKind.DeleteBackward);
        var spoken = ui.Spoken();
        Assert.Contains(spoken, s => s.Contains("browse entry"));
        Assert.Equal("browse entry", list.SelectedItem?.Value);
    }

    [Fact]
    public void ALateArrivalReadsAsTheCursorLanding()
    {
        using var ui = new TestApp();
        var list = new LiveList(ui.App);
        list.Focus();
        ui.Drain();

        // The source gains an item outside any filter change: the
        // cursor, on nothing, lands on it, and that is heard.
        list.Source = _ => new[] { new Item("late arrival", 1) };
        Assert.Contains(ui.Spoken(), s => s.Contains("late arrival"));
    }

    [Fact]
    public void SelectionIdentitySurvivesAReorderingQuery()
    {
        using var ui = new TestApp();
        var alpha = new Item("alpha", 3);
        var beta = new Item("beta", 2);
        var list = new LiveList(ui.App)
        {
            Source = _ => new[] { alpha, beta },
        };
        list.Focus();
        ui.Type('x');
        ui.Input(InputKind.MoveDown); // onto beta
        ui.Drain();

        // New arrivals outrank beta; the cursor stays on beta anyway,
        // and only its place is news.
        list.Source = _ => new[] { new Item("newcomer", 9), alpha, beta };
        Assert.Equal(new[] { "3 of 3" }, ui.Spoken());
        Assert.Same(beta, list.SelectedItem);
    }

    [Fact]
    public void OnFilterChangedSwapsAStoredPoolBeforeTheReading()
    {
        using var ui = new TestApp();
        var list = new SwappingList(ui.App)
        {
            Source = filter => filter.Length == 0
                ? Array.Empty<Item>()
                : new[] { new Item($"result for {filter}", 2) },
        };
        list.Focus();

        ui.Type('x');
        Assert.Equal(new[] { "result for x 1 of 1" }, ui.Spoken());
    }
}
