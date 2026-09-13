using Srui;
using Xunit;

namespace Srui.Tests;

/// <summary>What a FilterListBox costs per tick: its results are one
/// computation per tick-end description, into buffers it keeps, and
/// fresh on every read program code makes — so a bound pool is asked
/// once per tick, and a pool an engine already ranked is never copied
/// or re-sorted.</summary>
public partial class FilterCacheTests
{
    private sealed partial class Entry : Element
    {
        public Entry(string text, int rank)
        {
            Value = text;
            Rank = rank;
        }

        [Field] public partial string? Value { get; set; }

        public int Rank { get; }
    }

    /// <summary>A filter list over a bound pool that counts how often
    /// the list asks for it.</summary>
    private sealed class CountingList : FilterListBox<Entry>
    {
        public int Reads;

        public List<Entry> Pool { get; } = new();

        public CountingList(IWidgetContainer parent) : base(parent, "Names", Array.Empty<Entry>())
        {
            Score = static (item, _) => item.Rank;
            BindItems(() =>
            {
                Reads++;
                return Pool;
            });
        }

        public void Refresh() => Touch();
    }

    private static (TestApp Ui, CountingList List) CountingUi(params string[] names)
    {
        var ui = new TestApp();
        var list = new CountingList(ui.App);
        var rank = names.Length;
        foreach (var name in names)
            list.Pool.Add(new Entry(name, rank--));
        list.Focus();
        ui.Drain();
        list.Reads = 0;
        return (ui, list);
    }

    [Fact]
    public void ATickEndAsksABoundPoolOnce()
    {
        var (ui, list) = CountingUi("alpha", "beta", "gamma");

        // Count, Position, and the item under the cursor all derive
        // from the results; one description is one computation.
        list.Pool.Add(new Entry("delta", 0));
        list.Refresh();
        ui.App.DispatchEvents();
        Assert.Equal(1, list.Reads);
    }

    [Fact]
    public void AKeystrokeAsksABoundPoolTwice()
    {
        var (ui, list) = CountingUi("alpha", "beta", "gamma");

        // Once resolving the cursor inside the input, once at the tick
        // end — the input's own reads are fresh, since the handler may
        // reshape the pool as it goes.
        ui.App.HandleInput(InputEvent.TypeChar('a'));
        ui.App.DispatchEvents();
        Assert.Equal(2, list.Reads);

        list.Reads = 0;
        ui.App.HandleInput(InputEvent.Simple(InputKind.MoveDown));
        ui.App.DispatchEvents();
        Assert.Equal(2, list.Reads);
    }

    [Fact]
    public void AReadFromProgramCodeIsAlwaysFresh()
    {
        var (_, list) = CountingUi("alpha");
        Assert.Equal(1, list.Results.Count);

        // No Touch, no tick: the pool changed under the list and the
        // very next read sees it.
        list.Pool.Add(new Entry("beta", 0));
        Assert.Equal(2, list.Results.Count);
        Assert.Equal(2, list.Reads);
    }

    [Fact]
    public void AnEmptyFilterIsThePoolItself()
    {
        var ui = new TestApp();
        var list = new FilterListBox<Entry>(ui.App, "Names", [new Entry("alpha", 1)]);
        Assert.Same(list.Items, list.Results);
    }

    [Fact]
    public void TheResultsBufferIsReusedAcrossFilters()
    {
        var ui = new TestApp();
        var list = new FilterListBox<Entry>(ui.App, "Names",
            [new Entry("alpha", 1), new Entry("alps", 2), new Entry("beta", 3)]);
        list.Focus();

        ui.Type('l');
        var first = list.Results;
        Assert.Equal(new[] { "alps", "alpha" }, first.Select(e => e.Value));

        ui.Type('p');
        // The same buffer, refilled: the view is valid until the next
        // change, not a fresh list per read.
        Assert.Same(first, list.Results);
        Assert.Equal(new[] { "alps", "alpha" }, list.Results.Select(e => e.Value));
    }

    [Fact]
    public void RankedKeepsPoolOrderAndScoreOnlyExcludes()
    {
        var ui = new TestApp();
        var list = new FilterListBox<Entry>(ui.App, "Names",
            [new Entry("beta", 1), new Entry("alpha", 5), new Entry("hidden", 9)])
        {
            Ranked = true,
            Score = static (item, _) => item.Rank == 9 ? null : item.Rank,
        };
        list.Focus();

        ui.Type('x');
        // Unranked, alpha's higher score would put it first.
        Assert.Equal(new[] { "beta", "alpha" }, list.Results.Select(e => e.Value));
        Assert.Equal(new[] { "beta 1 of 2" }, ui.Spoken());
    }

    [Fact]
    public void ARankedPoolKeptWholeIsTheResultsWithoutACopy()
    {
        var ui = new TestApp();
        var list = new FilterListBox<Entry>(ui.App, "Names",
            [new Entry("beta", 1), new Entry("alpha", 5)])
        {
            Ranked = true,
            Score = static (item, _) => item.Rank,
        };
        list.Focus();

        ui.Type('x');
        Assert.Same(list.Items, list.Results);
    }
}
