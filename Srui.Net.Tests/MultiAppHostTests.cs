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

    [Fact]
    public void FocusOnlyActivationSkipsTheName()
    {
        var (ui, notes, inbox, _, _) = TwoApps();
        ui.Host.Activate(notes);
        ui.Drain();

        ui.Host.Activate(inbox, SwitchAnnouncement.FocusOnly);
        var spoken = ui.Spoken();
        var utterance = Assert.Single(spoken);
        Assert.DoesNotContain("Inbox", utterance);
        Assert.StartsWith("Messages", utterance);
        Assert.True(inbox.IsActive);
    }

    [Fact]
    public void SilentActivationOfAVisitedAppSpeaksNothing()
    {
        var (ui, notes, inbox, _, _) = TwoApps();
        ui.Host.Activate(inbox);
        ui.Drain();
        ui.Host.Activate(notes);
        ui.Drain();

        ui.Host.Activate(inbox, SwitchAnnouncement.Silent);
        Assert.Empty(ui.Spoken());
        Assert.True(inbox.IsActive);
    }

    [Fact]
    public void ARenamedAppSpeaksItsNewNameFromTheNextSwitch()
    {
        var (ui, notes, inbox, _, _) = TwoApps();
        ui.Host.Activate(inbox);
        notes.Name = "Notes — draft.txt";
        ui.Drain();

        ui.Host.Activate(notes);
        Assert.Equal("Notes — draft.txt", ui.Spoken()[0]);
    }
}

public class HostedAppLifecycleTests
{
    private static (MultiTestUi Ui, HostedApp A, HostedApp B, HostedApp C) ThreeApps()
    {
        var ui = new MultiTestUi();
        var a = ui.Host.Add("Alpha");
        _ = new Button(a.App, "A");
        var b = ui.Host.Add("Beta");
        _ = new Button(b.App, "B");
        var c = ui.Host.Add("Gamma");
        _ = new Button(c.App, "C");
        return (ui, a, b, c);
    }

    [Fact]
    public void ClosingABackgroundAppIsSilentAndLeavesTheList()
    {
        var (ui, a, b, _) = ThreeApps();
        ui.Host.Activate(a);
        ui.Drain();

        b.Close();
        Assert.Empty(ui.Spoken());
        Assert.True(b.IsClosed);
        Assert.Equal(2, ui.Host.Apps.Count);
        Assert.DoesNotContain(b, ui.Host.Apps);
        Assert.True(a.IsActive);
    }

    [Fact]
    public void ClosingTheActiveAppActivatesItsNeighbor()
    {
        var (ui, a, b, c) = ThreeApps();
        ui.Host.Activate(b);
        ui.Drain();

        b.Close();
        Assert.True(c.IsActive);
        Assert.True(c.App.IsForeground);
        Assert.False(b.App.IsForeground);
        Assert.Equal(new[] { "Gamma", "C button" }, ui.Spoken());
        _ = a;
    }

    [Fact]
    public void ClosingTheLastAppLeavesNoActive()
    {
        var ui = new MultiTestUi();
        var only = ui.Host.Add("Only");
        _ = new Button(only.App, "O");
        ui.Host.Activate(only);
        ui.Drain();

        only.Close();
        Assert.Null(ui.Host.Active);
        Assert.Empty(ui.Host.Apps);
        Assert.False(ui.Host.HandleInput(InputEvent.TypeChar('x')));
    }

    [Fact]
    public void CloseIsIdempotentAndDropsLaterSends()
    {
        var (ui, _, b, _) = ThreeApps();
        var closedEvents = 0;
        b.Closed += () => closedEvents++;
        var received = 0;
        b.MessageReceived += _ => received++;

        b.Close();
        b.Close();
        Assert.Equal(1, closedEvents);

        b.Send("late");
        ui.Host.DispatchEvents();
        Assert.Equal(0, received);
    }

    [Fact]
    public void ActivatingAClosedAppThrows()
    {
        var (ui, _, b, _) = ThreeApps();
        b.Close();
        Assert.Throws<InvalidOperationException>(() => ui.Host.Activate(b));
    }

    [Fact]
    public void SwitchingSkipsClosedApps()
    {
        var (ui, a, b, c) = ThreeApps();
        ui.Host.Activate(a);
        b.Close();
        ui.Drain();

        var (key, mods) = KeyCombo.WithCtrl(Key.Tab).ToFlat();
        ui.Host.HandleInput(InputEvent.RawKey(key, mods));
        Assert.True(c.IsActive);
    }

    [Fact]
    public void AnAppCanCloseItselfFromItsOwnHandler()
    {
        var (ui, a, b, _) = ThreeApps();
        var quitButton = new Button(a.App, "Exit Alpha");
        quitButton.Activated += () => a.Close();
        ui.Host.Activate(a);
        quitButton.Focus();
        ui.Drain();

        // Enter presses the focused button; its handler runs at drain
        // and closes the app that is dispatching.
        ui.Host.HandleInput(InputEvent.Simple(InputKind.Activate));
        ui.Host.DispatchEvents();
        Assert.True(a.IsClosed);
        Assert.True(b.IsActive);
        Assert.Contains("Beta", ui.Spoken());
    }

    [Fact]
    public void AHostedAppsQuitClosesItOnTheNextTick()
    {
        var (ui, a, b, _) = ThreeApps();
        ui.Host.Activate(a);
        ui.Drain();

        a.App.Quit();
        ui.Host.Tick();
        Assert.True(a.IsClosed);
        Assert.True(b.IsActive);
    }

    [Fact]
    public void AppsCanBeAddedFromARunningHandler()
    {
        var (ui, a, _, _) = ThreeApps();
        var spawn = new Button(a.App, "Spawn");
        HostedApp? spawned = null;
        spawn.Activated += () =>
        {
            spawned = ui.Host.Add("Delta");
            _ = new Button(spawned.App, "D");
        };
        ui.Host.Activate(a);
        spawn.Focus();
        ui.Drain();

        ui.Host.HandleInput(InputEvent.Simple(InputKind.Activate));
        ui.Host.DispatchEvents();
        Assert.NotNull(spawned);
        Assert.Equal(4, ui.Host.Apps.Count);
    }

    [Fact]
    public void AppsChangedFiresOnAddAndClose()
    {
        var ui = new MultiTestUi();
        var changes = 0;
        ui.Host.AppsChanged += () => changes++;
        var app = ui.Host.Add("One");
        Assert.Equal(1, changes);
        app.Close();
        Assert.Equal(2, changes);
    }
}

public class HostReservationTests
{
    [Fact]
    public void HostedAppsRefuseTheSwitchingCombos()
    {
        var ui = new MultiTestUi();
        var app = ui.Host.Add("One").App;

        Assert.Equal(
            "control tab is reserved for switching apps",
            app.ReservedReasonFor(KeyCombo.WithCtrl(Key.Tab)));
        Assert.Equal(
            "control shift tab is reserved for switching apps",
            app.ReservedReasonFor(KeyCombo.CtrlShift(Key.Tab)));
        Assert.Null(app.ReservedReasonFor(KeyCombo.WithCtrl(Key.Char('s'))));
    }

    [Fact]
    public void ReservationFollowsReconfiguredCombos()
    {
        var ui = new MultiTestUi();
        var app = ui.Host.Add("One").App;
        ui.Host.NextAppCombo = KeyCombo.WithCtrl(Key.F(6));

        Assert.Null(app.ReservedReasonFor(KeyCombo.WithCtrl(Key.Tab)));
        Assert.NotNull(app.ReservedReasonFor(KeyCombo.WithCtrl(Key.F(6))));
    }

    [Fact]
    public void FrameworkReservationsStillApply()
    {
        var ui = new MultiTestUi();
        var app = ui.Host.Add("One").App;
        Assert.Equal(
            KeyCombo.Plain(Key.Tab).ReservedReason,
            app.ReservedReasonFor(KeyCombo.Plain(Key.Tab)));
    }

    [Fact]
    public void StandaloneAppsHaveNoHostReservations()
    {
        using var app = SruiApp.Headless();
        Assert.Null(app.ReservedReasonFor(KeyCombo.WithCtrl(Key.Tab)));
    }

    [Fact]
    public void RequestQuitConsultsTheHandler()
    {
        using var ui = new MultiTestUi();
        _ = ui.Host.Add("Notes");

        var consent = false;
        var asked = 0;
        ui.Host.QuitRequested = () =>
        {
            asked++;
            return consent;
        };

        Assert.False(ui.Host.RequestQuit());
        Assert.Equal(1, asked);
        // The loop keeps running after a refusal.
        Assert.True(ui.Host.Tick());

        consent = true;
        Assert.True(ui.Host.RequestQuit());
        Assert.Equal(2, asked);
        Assert.False(ui.Host.Tick());
    }

    [Fact]
    public void RequestQuitWithoutAHandlerQuits()
    {
        using var ui = new MultiTestUi();
        _ = ui.Host.Add("Notes");
        Assert.True(ui.Host.RequestQuit());
        Assert.False(ui.Host.Tick());
    }
}
