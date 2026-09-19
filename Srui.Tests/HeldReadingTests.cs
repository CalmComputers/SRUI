using Srui;
using Xunit;

namespace Srui.Tests;

/// <summary>The held reading (architecture.md section 7.2): a program
/// that tells a change over time holds the state reading, so the
/// landing its first tick produced is heard after the story, not in
/// the middle of it. The hold keeps the heard baseline where it was,
/// so whenever it ends — released by the program, or by a tick of the
/// user's own that would read something — the withheld change is
/// heard as part of one ordinary reading. A keypress that reads
/// nothing leaves the hold standing; a dialog never opens under
/// one.</summary>
public class HeldReadingTests
{
    private static (TestApp Ui, ListBox List) ListUi()
    {
        var ui = new TestApp();
        var list = new ListBox(ui.App, "Files", ["a", "b", "c"], numbered: true);
        list.Focus();
        ui.Drain();
        return (ui, list);
    }

    [Fact]
    public void AHeldLandingIsHeardAfterTheStoryWhenReleased()
    {
        var (ui, list) = ListUi();

        // The program's change and the story's first line, one tick.
        ui.App.HoldReading();
        list.SelectedIndex = 1;
        ui.App.Announce("First line.");
        Assert.Equal(new[] { "First line." }, ui.Spoken());
        Assert.True(ui.App.ReadingHeld);

        // The story continues from a later tick: still no landing.
        ui.App.Announce("Second line.");
        Assert.Equal(new[] { "Second line." }, ui.Spoken());

        // The story ends; the landing reads as the cursor move it was.
        ui.App.ReleaseReading();
        Assert.Equal(new[] { "b 2 of 3" }, ui.Spoken());
        Assert.False(ui.App.ReadingHeld);

        // Nothing left over.
        ui.App.ReleaseReading();
        Assert.Empty(ui.Spoken());
    }

    [Fact]
    public void AUsersMoveEndsTheHoldAndReadsWhereTheyAre()
    {
        var (ui, list) = ListUi();
        ui.App.HoldReading();
        list.SelectedIndex = 1;
        ui.App.Announce("First line.");
        Assert.Equal(new[] { "First line." }, ui.Spoken());

        // Down from the withheld position: the user hears where they
        // landed, measured from what they last heard — one reading.
        ui.Input(InputKind.MoveDown);
        Assert.Equal(new[] { "c 3 of 3" }, ui.Spoken());
        Assert.False(ui.App.ReadingHeld);

        // The story's end has nothing more to say about focus.
        ui.App.ReleaseReading();
        Assert.Empty(ui.Spoken());
    }

    [Fact]
    public void AnEdgeHitEndsTheHold()
    {
        var (ui, list) = ListUi();
        list.SelectedIndex = 1;
        ui.Drain();                                  // heard: b

        ui.App.HoldReading();
        list.SelectedIndex = 0;
        Assert.Empty(ui.Spoken());

        // Up at the top moves nothing, but the edge reads, and the
        // item with it — so the user hears where they are.
        ui.Input(InputKind.MoveUp);
        var heard = ui.Spoken();
        Assert.Contains(heard, s => s.Contains("a 1 of 3"));
        Assert.False(ui.App.ReadingHeld);
    }

    [Fact]
    public void AKeyThatReadsNothingLeavesTheHoldStanding()
    {
        var (ui, list) = ListUi();
        var commands = 0;
        ui.App.UnhandledInput = input =>
        {
            commands++;
            ui.App.Announce("Boss: The Wall.");   // a global command that only announces
            return true;
        };

        ui.App.HoldReading();
        list.SelectedIndex = 1;
        Assert.Empty(ui.Spoken());

        // An unbound raw key: nothing claims it, nothing reads.
        ui.Input(InputEvent.RawKey(Key.F(9).Code, Mods.Ctrl));
        Assert.Equal(new[] { "Boss: The Wall." }, ui.Spoken());
        Assert.Equal(1, commands);
        Assert.True(ui.App.ReadingHeld);

        // Speak-focus is the user asking to hear where they are.
        ui.Input(InputKind.SpeakFocus);
        Assert.Equal(new[] { "Files list b 2 of 3" }, ui.Spoken());
        Assert.False(ui.App.ReadingHeld);
    }

    [Fact]
    public void TheTickThatHoldsIsWithheldEvenWhenTheUserCausedIt()
    {
        var ui = new TestApp();
        var list = new ListBox(ui.App, "Files", ["a", "b", "c"], numbered: true, activateItems: true);
        list.Focus();
        ui.Drain();

        // Enter on an item: the program acts on it, moves the cursor,
        // and holds — in the tick the user's key caused.
        list.Activated += () =>
        {
            list.SelectedIndex = 2;
            ui.App.Announce("Used a.");
            ui.App.HoldReading();
        };
        ui.Input(InputKind.Activate);
        Assert.Equal(new[] { "Used a." }, ui.Spoken());
        Assert.True(ui.App.ReadingHeld);

        ui.App.ReleaseReading();
        Assert.Equal(new[] { "c 3 of 3" }, ui.Spoken());
    }

    [Fact]
    public void ANewHoldDuringAHoldWithholdsItsOwnTick()
    {
        var (ui, list) = ListUi();
        ui.App.HoldReading();
        list.SelectedIndex = 1;
        Assert.Empty(ui.Spoken());

        // A second story starts before the first ends (an overlapping
        // action): its tick is withheld too, and one release reads
        // the settled position once.
        ui.App.HoldReading();
        list.SelectedIndex = 2;
        ui.App.Announce("Again.");
        Assert.Equal(new[] { "Again." }, ui.Spoken());

        ui.App.ReleaseReading();
        Assert.Equal(new[] { "c 3 of 3" }, ui.Spoken());
    }

    [Fact]
    public void ADialogOpeningEndsTheHold()
    {
        var (ui, list) = ListUi();
        ui.App.HoldReading();
        list.SelectedIndex = 1;
        Assert.Empty(ui.Spoken());

        var dialog = ui.App.OpenDialog();
        var ok = new Button(dialog, "OK");
        ok.Focus();
        dialog.AnnounceOpened();
        Assert.Contains(ui.Spoken(), s => s.Contains("OK button"));
        Assert.False(ui.App.ReadingHeld);

        // Closing restores the ground, read as the restore it is —
        // the withheld position is what the user finds there.
        dialog.Close();
        Assert.Contains(ui.Spoken(), s => s.Contains("b 2 of 3"));
    }

    [Fact]
    public void AFocusMoveUnderTheHoldReadsAsAnArrivalOnRelease()
    {
        var ui = new TestApp();
        var list = new ListBox(ui.App, "Files", ["a", "b"], numbered: true);
        var button = new Button(ui.App, "Play");
        list.Focus();
        ui.Drain();

        ui.App.HoldReading();
        button.Focus();
        ui.App.Announce("Hand played.");
        Assert.Equal(new[] { "Hand played." }, ui.Spoken());

        ui.App.ReleaseReading();
        Assert.Equal(new[] { "Play button" }, ui.Spoken());
    }
}
