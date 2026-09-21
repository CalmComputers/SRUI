using Srui;
using Xunit;

namespace Srui.Tests;

/// <summary>The tick model (architecture.md section 7): a tick's
/// announcements and action events reach readers in order, and its
/// state is read once at the end, as the difference between what the
/// user last heard and what is now under the cursor — however many
/// times, and in whatever order, program code changed things in
/// between. Suppress and Reread are the two knobs a widget has on that
/// reading.</summary>
public partial class TickModelTests
{
    [Fact]
    public void OnlyTheSettledStateIsRead()
    {
        using var ui = new TestApp();
        var list = new ListBox(ui.App, "Files", ["a", "b", "c"], numbered: true);
        list.Focus();
        ui.Drain();

        // Three moves in one tick: one reading, of where the cursor ended.
        list.SelectedIndex = 1;
        list.SelectedIndex = 2;
        list.SelectedIndex = 0;
        Assert.Empty(ui.Spoken());

        list.SelectedIndex = 2;
        list.SelectedIndex = 1;
        Assert.Equal(new[] { "b 2 of 3" }, ui.Spoken());
    }

    [Fact]
    public void FocusThenStateReadsTheSettledValueOnce()
    {
        using var ui = new TestApp();
        _ = new Button(ui.App, "Other");
        var list = new ListBox(ui.App, "Files", ["a", "b"], numbered: true);
        ui.App.EnsureFocus();
        ui.Drain();

        // Either order: the focus reading carries the state as it
        // settled, and the state change is not read again.
        list.Focus();
        list.SelectedIndex = 1;
        Assert.Equal(new[] { "Files list b 2 of 2" }, ui.Spoken());

        ui.App.FocusedWidget!.App.EnsureFocus();
        new Button(ui.App, "Elsewhere").Focus();
        ui.Drain();
        list.SelectedIndex = 0;
        list.Focus();
        Assert.Equal(new[] { "Files list a 1 of 2" }, ui.Spoken());
    }

    [Fact]
    public void AnnouncementsSpeakBeforeTheStateWhateverTheOrder()
    {
        using var ui = new TestApp();
        var list = new ListBox(ui.App, "Files", ["a", "b"], numbered: true);
        list.Focus();
        ui.Drain();

        list.SelectedIndex = 1;
        ui.App.Announce("Moved.");
        Assert.Equal(new[] { "Moved.", "b 2 of 2" }, ui.Spoken());
    }

    [Fact]
    public void AWidgetsAnnouncementSpeaksOnlyWhereFocusSettles()
    {
        using var ui = new TestApp();
        var box = new AddBox(ui.App);
        var list = new ListBox(ui.App, "Items", ["one"]);
        box.Focus();
        ui.Drain();

        // Confirmation that stays: heard.
        box.Confirm(moveOn: false);
        Assert.Equal(new[] { "Added." }, ui.Spoken());

        // Confirmation that moves focus to the result: the landing is
        // the confirmation, and the words are not.
        box.Confirm(moveOn: true, list);
        Assert.Equal(new[] { "Items list one" }, ui.Spoken());
    }

    private sealed class AddBox(IWidgetContainer parent) : EditBox(parent, "Add")
    {
        public void Confirm(bool moveOn, Widget? target = null)
        {
            Announce("Added.");
            if (moveOn)
                target!.Focus();
        }
    }

    [Fact]
    public void AContainersAnnouncementSpeaksForTheChildFocusIsIn()
    {
        using var ui = new TestApp();
        var panel = new Panel(ui.App);
        var elsewhere = new Button(ui.App, "Elsewhere");
        panel.Entry.Focus();
        ui.Drain();

        // The composite speaks for what it holds.
        panel.Say("Added.");
        Assert.Equal(new[] { "Added." }, ui.Spoken());

        // Not for a widget outside it.
        elsewhere.Focus();
        ui.Drain();
        panel.Say("Added.");
        Assert.Empty(ui.Spoken());
    }

    private sealed class Panel : Group
    {
        public EditBox Entry { get; }

        public Panel(IWidgetContainer parent) : base(parent, "Tasks") => Entry = new EditBox(this, "New task");

        public void Say(string text) => Announce(text);
    }

    [Fact]
    public void RereadWinsOverSuppressInTheSameTick()
    {
        using var ui = new TestApp();
        var save = new Button(ui.App, "Save");
        save.Focus();
        ui.Drain();

        save.Suppress(Fields.Name);
        save.Name = "Store";
        save.Reread(Fields.Name);
        Assert.Equal(new[] { "Store" }, ui.Spoken());
    }

    [Fact]
    public void AppAnnouncementsAlwaysSpeak()
    {
        using var ui = new TestApp();
        var box = new EditBox(ui.App, "Add");
        var list = new ListBox(ui.App, "Items", ["one"]);
        box.Focus();
        ui.Drain();

        ui.App.Announce("Added.");
        list.Focus();
        Assert.Equal(new[] { "Added.", "Items list one" }, ui.Spoken());
    }

    [Fact]
    public void ActionEventsOfTheArrivingWidgetYieldToItsReading()
    {
        using var ui = new TestApp();
        var notes = new EditBox(ui.App, "Notes", "hello");
        var other = new Button(ui.App, "Other");
        other.Focus();
        ui.Drain();

        // Prepared and then focused in one tick: the reading is the
        // whole utterance, and the move's word is not heard before it.
        notes.MoveWordRight();
        notes.Focus();
        Assert.Equal(new[] { "Notes edit selected hello" }, ui.Spoken());
    }

    [Fact]
    public void TheArrivingWidgetsOwnAnnouncementIsHeardBeforeItsReading()
    {
        using var ui = new TestApp();
        var box = new AddBox(ui.App);
        var other = new Button(ui.App, "Other");
        other.Focus();
        ui.Drain();

        // A result announced by the widget focus then lands on (a
        // pane renaming from its dialog, restored when it closes) is
        // deliberate, unlike a move made on the way: it speaks, then
        // the landing does.
        box.Confirm(moveOn: true, box);
        Assert.Equal(new[] { "Added.", "Add edit blank" }, ui.Spoken());
    }

    [Fact]
    public void AWidgetSpeakingThroughAContainerIsHeardBesideItsSibling()
    {
        using var ui = new TestApp();
        var pane = new Group(ui.App, "Console");
        var input = new EditBox(pane, "Input");
        var output = new EditBox(pane, "Output", multiline: true);
        output.Focus();
        ui.Drain();

        // Driven while the user is on its sibling: silent by default.
        input.InsertText("d");
        Assert.Empty(ui.Spoken());

        // Speaking through the pane both live in, its echo is heard
        // from anywhere inside the pane.
        input.SpeaksThrough = pane;
        input.InsertText("i");
        Assert.Equal(new[] { "i" }, ui.Spoken());
    }

    [Fact]
    public void ASuppressedFieldStillReadsWhereTheCursorLands()
    {
        using var ui = new TestApp();
        var list = new ListBox(ui.App, "Items", ["one", "two", "three"], numbered: true);
        list.Focus();
        ui.Drain();

        // A refresh that keeps in-place line changes out of the reading
        // (the program narrates those) must not silence a landing: the
        // item under the cursor went, and the survivor is news in full.
        list.Suppress(Fields.Value, Fields.Position);
        list.RemoveAt(0);
        Assert.Equal(new[] { "two 1 of 2" }, ui.Spoken());

        // The same suppression over a surviving item: silent, as asked.
        list.Suppress(Fields.Value, Fields.Position);
        list.SetItem(1, new ListItem("THREE"));
        list.RemoveAt(1);
        list.Insert(1, new ListItem("three"));
        Assert.Empty(ui.Spoken());
    }

    [Fact]
    public void ActionEventsOfAWidgetLeftBehindAreDropped()
    {
        using var ui = new TestApp();
        var notes = new EditBox(ui.App, "Notes", "hello");
        var other = new Button(ui.App, "Other");
        notes.Focus();
        ui.Drain();

        // The move speaks its word only if the user is still there.
        notes.MoveWordRight();
        other.Focus();
        Assert.Equal(new[] { "Other button" }, ui.Spoken());
    }

    [Fact]
    public void SuppressKeepsAFieldOutOfOneTicksReading()
    {
        using var ui = new TestApp();
        var save = new Button(ui.App, "Save");
        save.Focus();
        ui.Drain();

        save.Suppress(Fields.Name);
        save.Name = "Store";
        save.Description = "keeps the file";
        Assert.Equal(new[] { "keeps the file" }, ui.Spoken());

        // The next tick reads normally — and the suppressed change is
        // not caught up on: the user heard the settled state after it.
        save.Name = "Stash";
        Assert.Equal(new[] { "Stash" }, ui.Spoken());
    }

    [Fact]
    public void ASuppressionMadeBeforeTheLandingYieldsToIt()
    {
        using var ui = new TestApp();
        _ = new Button(ui.App, "Other");
        var save = new Button(ui.App, "Save");
        ui.App.EnsureFocus();
        ui.Drain();

        save.Suppress();
        save.Focus();
        Assert.Equal(new[] { "Save button" }, ui.Spoken());
    }

    [Fact]
    public void ASuppressionMadeAfterTheLandingTrimsTheArrival()
    {
        using var ui = new TestApp();
        _ = new Button(ui.App, "Other");
        var save = new Button(ui.App, "Save");
        ui.App.EnsureFocus();
        ui.Drain();

        // Whatever brought the user here already said the name.
        save.Focus();
        save.Suppress(Fields.Name);
        Assert.Equal(new[] { "button" }, ui.Spoken());

        // For that tick alone.
        ui.App.ReannounceWithContext();
        Assert.Equal(new[] { "Save button" }, ui.Spoken());
    }

    [Fact]
    public void AReannouncementIsAnArrivalToo()
    {
        using var ui = new TestApp();
        var save = new Button(ui.App, "Save");
        ui.App.EnsureFocus();
        ui.Drain();

        // Before the request yields to it; after it trims it.
        save.Suppress();
        ui.App.ReannounceWithContext();
        Assert.Equal(new[] { "Save button" }, ui.Spoken());

        ui.App.ReannounceWithContext();
        save.Suppress(Fields.Name);
        Assert.Equal(new[] { "button" }, ui.Spoken());
    }

    [Fact]
    public void RereadPutsAnUnchangedFieldIntoTheReading()
    {
        using var ui = new TestApp();
        var volume = new Slider(ui.App, "Volume", 50, 0, 100, unit: "%");
        volume.Focus();
        ui.Drain();

        volume.Reread(Fields.Number);
        Assert.Equal(new[] { "50%" }, ui.Spoken());
        Assert.Empty(ui.Spoken());
    }

    [Fact]
    public void ALandedItemReadsInFullEvenWhereItsFieldsMatch()
    {
        using var ui = new TestApp();
        var list = new ListBox(ui.App, "Fruits", ["apple", "apple"], multiSelect: true);
        list.SetChecked(0, true);
        list.SetChecked(1, true);
        list.Focus();
        ui.Drain();

        // Same line, same check: a different item, so it is read.
        ui.Input(InputKind.MoveDown);
        Assert.Equal(new[] { "apple checked" }, ui.Spoken());
    }

    [Fact]
    public void AChangeThatRevertsWithinTheTickIsSilent()
    {
        using var ui = new TestApp();
        var wrap = new CheckBox(ui.App, "Wrap");
        wrap.Focus();
        ui.Drain();

        wrap.Checked = true;
        wrap.Checked = false;
        Assert.Empty(ui.Spoken());
    }

    [Fact]
    public void AnIdleLoopIterationReadsNothing()
    {
        using var ui = new TestApp();
        var list = new ListBox(ui.App, "Files", ["a"]);
        list.Focus();
        ui.Drain();

        // A field write on an item is not seen by the engine, and a
        // loop iteration where nothing happened does not describe the
        // focused widget; the explicit dispatch does.
        list.Items[0].Value = "b";
        ui.App.TickAt(ui.App.Now + 1);
        Assert.Empty(ui.Reader.Ticks);
        ui.App.DispatchEvents();
        Assert.Equal(new[] { "b" }, ui.Spoken());
    }

    [Fact]
    public void BoundFieldsWriteThemselves()
    {
        using var ui = new TestApp();
        var model = new { Volume = 40.0 };
        var volume = 40.0;
        var slider = new Slider(ui.App, "Volume", 0, 0, 100, unit: "%");
        slider.Bind(Fields.Number, () => volume, v => volume = v);
        slider.Focus();
        Assert.Equal(new[] { "Volume slider 40%" }, ui.Spoken());

        // Input writes the model through the setter...
        ui.Input(InputKind.MoveRight);
        Assert.Equal(41.0, volume);
        Assert.Equal(new[] { "41%" }, ui.Spoken());

        // ...and the model changing underneath is read at the tick end.
        volume = 90;
        Assert.Equal(new[] { "90%" }, ui.Spoken());
        _ = model;
    }

    [Fact]
    public void AReadOnlyBindingRefusesWrites()
    {
        using var ui = new TestApp();
        var slider = new Slider(ui.App, "Volume", 0, 0, 100);
        slider.Bind(Fields.Number, () => 5);
        Assert.Equal(5, slider.Number);
        Assert.Throws<InvalidOperationException>(() => slider.Number = 6);
    }

    [Fact]
    public void ACustomFieldSpeaksAsItsProgramRegisteredIt()
    {
        using var ui = new TestApp();
        var meter = new Meter(ui.App);
        meter.Focus();
        // Unregistered: a string field speaks as it is, a number is
        // silent until a rendering says how it sounds.
        Assert.Equal(new[] { "Meter meter steady" }, ui.Spoken());
        Assert.NotNull(Meter.GaugeField);
        Assert.Equal("Gauge", Meter.GaugeField.Name);

        // Registered on the shared renderer, the number speaks — for
        // this test and any other in the process, so the wording is
        // one no other test could meet by accident — at the place its
        // declaration asked for: before the trend.
        SpeechRenderer.Default.Register(Meter.GaugeField, static (_, v) => $"gauge at {v}");
        meter.Gauge = 7;
        Assert.Equal(new[] { "gauge at 7" }, ui.Spoken());
        ui.Input(InputKind.SpeakFocus);
        Assert.Equal(new[] { "Meter meter gauge at 7 steady" }, ui.Spoken());
    }

    [Fact]
    public void AFieldThatReadsNullIsAbsentAndItsGoingIsADelta()
    {
        using var ui = new TestApp();
        var meter = new Meter(ui.App);
        meter.Focus();
        ui.Drain();

        // Absent: not in the description, so not a fact of the arrival.
        Assert.False(meter.Describe().Contains(Meter.NoteField));
        meter.Note = "hot";
        Assert.Equal(new[] { "hot" }, ui.Spoken());
        // (The gauge's rendering may or may not be registered on the
        // shared renderer by now, so only the tail is asserted.)
        ui.Input(InputKind.SpeakFocus);
        Assert.EndsWith("steady hot", Assert.Single(ui.Spoken()));

        // Going: a delta with no value, which the default rendering of
        // a string field makes nothing of.
        meter.Note = null;
        ui.App.DispatchEvents();
        Assert.Contains(ui.Reader.Ticks[^1],
            e => e is AccessibilityEvent.FieldValue { Field: var f, Value: null } && ReferenceEquals(f, Meter.NoteField));
        Assert.Empty(ui.Spoken());
    }

    private sealed partial class Meter(IWidgetContainer parent) : Widget(parent, "Meter", new Role("meter"))
    {
        [Field(Before = nameof(Trend))] public partial int Gauge { get; set; }

        [Field] public string Trend => "steady";

        /// <summary>A remark, or none.</summary>
        [Field(After = nameof(Trend))] public partial string? Note { get; set; }
    }
}
