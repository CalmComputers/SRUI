using Srui;
using Xunit;

namespace Srui.Net.Tests;

/// <summary>A headless multi-app host with a recording shared reader:
/// build apps, activate and switch, assert what the shared stream hears.</summary>
internal sealed class MultiTestUi : IDisposable
{
    public readonly MultiAppHost Host = MultiAppHost.Headless();
    public readonly TestReader Reader = new();

    public MultiTestUi() => Host.AddReader(Reader);

    public void Dispose() => Host.Dispose();

    /// <summary>Deliver queued messages and output, returning the
    /// utterances the shared reader heard since the last call.</summary>
    public List<string> Spoken()
    {
        Host.DispatchEvents();
        var result = Reader.Events
            .Select(SpeechRenderer.RenderEvent)
            .Where(s => s is not null)
            .Select(s => s!)
            .ToList();
        Reader.Events.Clear();
        return result;
    }

    /// <summary>Deliver queued messages and output, discarding it.</summary>
    public void Drain()
    {
        Host.DispatchEvents();
        Reader.Events.Clear();
    }

    public bool Combo(KeyCombo combo)
    {
        var (key, mods) = combo.ToFlat();
        return Host.HandleInput(InputEvent.RawKey(key, mods));
    }
}

public class MultiAppHostTests
{
    private sealed record Fixture(
        MultiTestUi Ui, HostedApp Notes, HostedApp Inbox, EditBox NoteBox, ListBox MessageList);

    private static Fixture TwoApps()
    {
        var ui = new MultiTestUi();
        var notes = ui.Host.Add("Notes");
        var noteBox = new EditBox(notes.App, "Note");
        var inbox = ui.Host.Add("Inbox");
        var messageList = new ListBox(inbox.App, "Messages", Array.Empty<string>());
        return new Fixture(ui, notes, inbox, noteBox, messageList);
    }

    [Fact]
    public void ActivateSpeaksNameThenFocusWithContext()
    {
        var (ui, notes, _, _, _) = TwoApps();
        ui.Host.Activate(notes);
        Assert.Equal(new[] { "Notes", "Note edit blank" }, ui.Spoken());
        Assert.True(notes.IsActive);
        Assert.True(notes.App.IsForeground);
    }

    [Fact]
    public void InputRoutesToTheActiveAppOnly()
    {
        var (ui, notes, inbox, noteBox, _) = TwoApps();
        ui.Host.Activate(notes);
        ui.Drain();

        Assert.True(ui.Host.HandleInput(InputEvent.TypeChar('x')));
        ui.Host.Activate(inbox);
        ui.Drain();
        ui.Host.HandleInput(InputEvent.TypeChar('y'));

        // The 'x' landed in Notes; the 'y' went to Inbox's list (as
        // typeahead), never into the backgrounded edit box.
        Assert.Equal("x", noteBox.Text);
    }

    [Fact]
    public void CtrlTabCyclesWithWraparound()
    {
        var (ui, notes, inbox, _, _) = TwoApps();
        ui.Host.Activate(notes);
        ui.Drain();

        Assert.True(ui.Combo(KeyCombo.WithCtrl(Key.Tab)));
        Assert.True(inbox.IsActive);
        Assert.Contains("Inbox", ui.Spoken());

        Assert.True(ui.Combo(KeyCombo.WithCtrl(Key.Tab)));
        Assert.True(notes.IsActive);

        Assert.True(ui.Combo(KeyCombo.CtrlShift(Key.Tab)));
        Assert.True(inbox.IsActive);
    }

    [Fact]
    public void SwitchingCombosAreConfigurable()
    {
        var (ui, notes, inbox, _, _) = TwoApps();
        ui.Host.Activate(notes);
        ui.Drain();
        ui.Host.NextAppCombo = KeyCombo.WithCtrl(Key.F(6));

        Assert.False(ui.Combo(KeyCombo.WithCtrl(Key.Tab)));
        Assert.True(notes.IsActive);
        Assert.True(ui.Combo(KeyCombo.WithCtrl(Key.F(6))));
        Assert.True(inbox.IsActive);

        ui.Host.NextAppCombo = null;
        Assert.False(ui.Combo(KeyCombo.WithCtrl(Key.F(6))));
    }

    [Fact]
    public void BackgroundAppsAreMuted()
    {
        var (ui, notes, inbox, _, _) = TwoApps();
        ui.Host.Activate(notes);
        ui.Drain();

        inbox.App.Announce("new message");
        Assert.Empty(ui.Spoken());
    }

    [Fact]
    public void OptedInBackgroundAnnouncementsAreHeard()
    {
        var (ui, notes, inbox, _, _) = TwoApps();
        inbox.AnnouncesInBackground = true;
        ui.Host.Activate(notes);
        ui.Drain();

        inbox.App.Announce("new message");
        Assert.Equal(new[] { "new message" }, ui.Spoken());
    }

    [Fact]
    public void OptInPassesOnlyAnnouncements()
    {
        var (ui, notes, inbox, _, messageList) = TwoApps();
        inbox.AnnouncesInBackground = true;
        ui.Host.Activate(inbox);
        ui.Drain();
        ui.Host.Activate(notes);
        ui.Drain();

        // A state change on the background app's focused widget emits a
        // list event, not an announcement: the shared reader stays quiet.
        messageList.Add("hello");
        Assert.Empty(ui.Spoken());
    }

    [Fact]
    public void PerAppReadersBypassTheSharedPolicy()
    {
        var (ui, notes, inbox, _, _) = TwoApps();
        var direct = new TestReader();
        inbox.App.AddReader(direct);
        ui.Host.Activate(notes);
        ui.Drain();

        inbox.App.Announce("for the log");
        ui.Host.DispatchEvents();
        Assert.Single(direct.Events);
    }

    [Fact]
    public void MessagesDeliverAtDrainTime()
    {
        var (ui, _, inbox, _, _) = TwoApps();
        var received = new List<object>();
        inbox.MessageReceived += received.Add;

        inbox.Send("hello");
        Assert.Empty(received);
        ui.Host.DispatchEvents();
        Assert.Equal(new object[] { "hello" }, received);
    }

    [Fact]
    public void SelfSendsWaitForTheNextDrain()
    {
        var (ui, _, inbox, _, _) = TwoApps();
        var delivered = 0;
        inbox.MessageReceived += _ =>
        {
            delivered++;
            if (delivered == 1)
                inbox.Send("again");
        };

        inbox.Send("first");
        ui.Host.DispatchEvents();
        Assert.Equal(1, delivered);
        ui.Host.DispatchEvents();
        Assert.Equal(2, delivered);
    }

    [Fact]
    public void DeactivationRunsFocusLostAndEvents()
    {
        var (ui, notes, inbox, _, _) = TwoApps();
        var notesFocusLost = 0;
        var notesDeactivated = 0;
        var inboxActivated = 0;
        notes.App.FocusLost = () => notesFocusLost++;
        notes.Deactivated += () => notesDeactivated++;
        inbox.Activated += () => inboxActivated++;

        ui.Host.Activate(notes);
        ui.Host.Activate(inbox);
        Assert.Equal(1, notesFocusLost);
        Assert.Equal(1, notesDeactivated);
        Assert.Equal(1, inboxActivated);
        Assert.False(notes.App.IsForeground);
        Assert.True(inbox.App.IsForeground);
    }

    [Fact]
    public void HostHooksFireAfterTheActiveAppDeclines()
    {
        var (ui, notes, _, _, _) = TwoApps();
        ui.Host.Activate(notes);
        ui.Drain();
        InputEvent? fell = null;
        ui.Host.UnhandledInput = input =>
        {
            fell = input;
            return true;
        };

        // An edit box consumes typing; a raw F9 falls all the way through.
        Assert.True(ui.Combo(KeyCombo.Plain(Key.F(9))));
        Assert.NotNull(fell);
    }

    [Fact]
    public void ActivateRejectsAForeignApp()
    {
        using var a = new MultiTestUi();
        using var b = new MultiTestUi();
        var foreign = b.Host.Add("Elsewhere");
        Assert.Throws<ArgumentException>(() => a.Host.Activate(foreign));
    }

    [Fact]
    public void QuitTurnsTickFalse()
    {
        var (ui, notes, _, _, _) = TwoApps();
        ui.Host.Activate(notes);
        Assert.True(ui.Host.Tick());
        ui.Host.Quit();
        Assert.False(ui.Host.Tick());
    }

    [Fact]
    public void InterruptReachesSharedReaders()
    {
        var (ui, notes, _, _, _) = TwoApps();
        ui.Host.Activate(notes);
        ui.Host.Interrupt();
        Assert.Equal(1, ui.Reader.Interrupts);
    }
}
