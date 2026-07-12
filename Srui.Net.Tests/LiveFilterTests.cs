using Srui;
using Xunit;

namespace Srui.Net.Tests;

/// <summary>The live-source seam on FilterListBox: OnFilterChanged runs
/// before the results report, so a subclass that re-queries an external
/// source per filter announces the fresh results, never the stale set.</summary>
public class LiveFilterTests
{
    private sealed class Item : IListItem
    {
        public Item(string text, int score)
        {
            Text = text;
            Score = score;
        }

        public string Text { get; }

        public int Score { get; }

        public int? FilterScore(string query) => Score;
    }

    /// <summary>A filter list over a fake live source: every filter
    /// change swaps the whole item set for what the "engine" returns.</summary>
    private sealed class LiveList : FilterListBox<Item>
    {
        public Func<string, IReadOnlyList<Item>> Source { get; set; } =
            _ => Array.Empty<Item>();

        public LiveList(IWidgetContainer parent)
            : base(parent, "Search", Array.Empty<Item>())
        {
        }

        protected override void OnFilterChanged(string filter) =>
            SetItemsSilently(Source(filter));

        /// <summary>A poll-driven swap, as a session draining async
        /// results would do.</summary>
        public void Refresh() => SetItemsSilently(Source(Filter));
    }

    [Fact]
    public void FilterReportReadsTheFreshlySwappedItems()
    {
        using var ui = new TestUi();
        var list = new LiveList(ui.App)
        {
            Source = filter => filter.Length == 0
                ? Array.Empty<Item>()
                : new[] { new Item($"result for {filter}", 2), new Item("runner-up", 1) },
        };
        list.Focus();
        ui.Drain();

        ui.Type('x');
        var spoken = ui.Spoken();
        Assert.Contains(spoken, s => s.Contains("result for x") && s.Contains("2"));

        Assert.Equal("result for x", list.SelectedItem?.Text);
    }

    [Fact]
    public void ErasingTheFilterSwapsBackThroughTheSameSeam()
    {
        using var ui = new TestUi();
        var browse = new[] { new Item("browse entry", 1) };
        var list = new LiveList(ui.App)
        {
            Source = filter => filter.Length == 0
                ? browse
                : new[] { new Item("match", 1) },
        };
        list.Focus();
        ui.Type('x');
        ui.Drain();

        ui.Input(InputKind.DeleteBackward);
        var spoken = ui.Spoken();
        Assert.Contains(spoken, s => s.Contains("browse entry"));
        Assert.Equal("browse entry", list.SelectedItem?.Text);
    }

    [Fact]
    public void SilentReplacementAloneSaysNothing()
    {
        using var ui = new TestUi();
        var list = new LiveList(ui.App);
        list.Focus();
        ui.Drain();

        // A poll-driven swap outside any filter change is the
        // subclass's own business to voice (or not).
        list.Source = _ => new[] { new Item("late arrival", 1) };
        list.Refresh();
        Assert.Empty(ui.Spoken());

        // The swapped items are live for focus and navigation.
        ui.Input(InputKind.SpeakFocus);
        Assert.Contains(ui.Spoken(), s => s.Contains("late arrival"));
    }
}
