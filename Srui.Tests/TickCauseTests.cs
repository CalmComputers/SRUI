using Srui;
using Xunit;

namespace Srui.Tests;

/// <summary>A tick is one cause: an input, a ticker firing, or the
/// program's own mutation between them. Two causes that share a loop
/// iteration read as two ticks, exactly as they would an iteration
/// apart — so what the user hears never depends on whether a timer
/// happened to land in the same 2 ms as a keystroke.</summary>
public class TickCauseTests
{
    /// <summary>A filter list over a pool the program mutates from a
    /// ticker, the way a streaming search list polls: the handler
    /// changes the pool and touches the list.</summary>
    private sealed class PolledList : FilterListBox
    {
        public List<ListItem> Pool { get; } = new();

        public PolledList(IWidgetContainer parent) : base(parent, "Names", Array.Empty<ListItem>())
        {
            BindItems(() => Pool);
        }

        public void Refresh() => Touch();

        public void Select(int index) => SelectedResultIndex = index;
    }

    private static (TestApp Ui, PolledList List) PolledUi(params string[] names)
    {
        var ui = new TestApp();
        var list = new PolledList(ui.App);
        foreach (var name in names)
            list.Pool.Add(new ListItem(name));
        list.Focus();
        ui.Drain();
        return (ui, list);
    }

    [Fact]
    public void AnInputAndADueTickerReadAsTwoTicks()
    {
        var (ui, list) = PolledUi("alpha", "beta", "gamma");
        ui.App.StartTicker(10).Tick += () =>
        {
            list.Pool.Add(new ListItem("delta"));
            list.Refresh();
        };

        // The ticker is due at this clock; the input lands in the same
        // iteration. The user hears the landing, then the growth.
        ui.App.SetNow(10);
        ui.App.HandleInput(InputEvent.Simple(InputKind.MoveDown));
        ui.App.DispatchEvents();

        Assert.Equal(2, ui.Reader.Ticks.Count);
        Assert.Equal(new[] { "beta 2 of 3", "2 of 4" }, ui.Spoken());
    }

    [Fact]
    public void ATickerUndoingTheInputsEffectStillLetsTheInputSpeak()
    {
        var (ui, list) = PolledUi("alpha", "beta", "gamma");
        ui.App.StartTicker(10).Tick += () =>
        {
            // The pool collapses to its first item: the cursor's item
            // is gone, and the survivor at its place is what the user
            // was on before the key.
            list.Pool.RemoveRange(1, 2);
            list.Refresh();
        };

        ui.App.SetNow(10);
        ui.App.HandleInput(InputEvent.Simple(InputKind.MoveDown));
        ui.App.DispatchEvents();

        // Folded into one tick this would be silence — alpha before,
        // alpha after. As two, the key answers and the collapse reads.
        Assert.Equal(new[] { "beta 2 of 3", "alpha 1 of 1" }, ui.Spoken());
    }

    [Fact]
    public void EachDueTickerReadsOnItsOwn()
    {
        var (ui, list) = PolledUi("alpha");
        ui.App.StartTicker(10).Tick += () =>
        {
            list.Pool.Add(new ListItem("beta"));
            list.Refresh();
        };
        ui.App.StartTicker(10).Tick += () =>
        {
            list.Pool.Add(new ListItem("gamma"));
            list.Refresh();
        };

        ui.Wait(10);
        Assert.Equal(new[] { "1 of 2", "1 of 3" }, ui.Spoken());
    }

    [Fact]
    public void ATickerFiringOnACleanEngineReadsNothing()
    {
        var (ui, _) = PolledUi("alpha");
        var fired = 0;
        ui.App.StartTicker(10).Tick += () => fired++;

        ui.Wait(10);
        Assert.Equal(1, fired);
        Assert.Empty(ui.Reader.Ticks);
    }

    [Fact]
    public void AProgramMutationBeforeATickerIsItsOwnTick()
    {
        var (ui, list) = PolledUi("alpha", "beta");
        ui.App.StartTicker(10).Tick += () =>
        {
            list.Pool.Add(new ListItem("gamma"));
            list.Refresh();
        };

        // Program code between iterations moves the cursor; the ticker
        // due at the next clock grows the pool. Two causes, two ticks.
        // (Driven raw: a harness step would drain the mutation's tick
        // away before advancing, by design.)
        list.Select(1);
        ui.App.TickAt(10);
        Assert.Equal(new[] { "beta 2 of 2", "2 of 3" }, ui.Spoken());
    }
}
