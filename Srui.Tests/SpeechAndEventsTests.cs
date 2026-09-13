using Srui;
using Srui.Testing;
using Xunit;

namespace Srui.Tests;

/// <summary>The reference speech rendering: a tick's state becomes one
/// utterance composed from the fields in NVDA order, and the verbosity
/// trims what is not information.</summary>
public class SpeechRendererTests
{
    private static List<string> Render(IReadOnlyList<AccessibilityEvent> events, SpeechVerbosity? verbosity = null) =>
        SpeechRenderer.Default.RenderTick(events, verbosity);

    private static readonly SruiApp App = SruiApp.Headless();

    private static List<AccessibilityEvent> Arrival(Widget widget, params ContextEntry[] context)
    {
        var events = new List<AccessibilityEvent>
        {
            new AccessibilityEvent.FocusArrived(widget, FocusCause.Programmatic, context),
        };
        foreach (var (field, value) in widget.Describe().Entries)
            events.Add(new AccessibilityEvent.FieldValue(widget, field, value, FieldScope.Control));
        if (widget.CurrentItem is { } item)
            foreach (var (field, value) in item.Describe().Entries)
                events.Add(new AccessibilityEvent.FieldValue(widget, field, value, FieldScope.Item));
        return events;
    }

    [Fact]
    public void AnnounceButtonWithShortcut()
    {
        var save = new Button(App, "Save");
        save.AddShortcut(KeyCombo.WithAlt(Key.Char('s')));
        save.AddShortcut(KeyCombo.WithCtrl(Key.Char('s')));
        // Only the first shortcut is announced.
        Assert.Equal(["Save button alt s"], Render(Arrival(save)));
    }

    [Fact]
    public void AnnounceCheckboxStates()
    {
        var wrap = new CheckBox(App, "Word Wrap");
        Assert.Equal(["Word Wrap check box not checked"], Render(Arrival(wrap)));
        wrap.Checked = true;
        Assert.Equal(["Word Wrap check box checked"], Render(Arrival(wrap)));
    }

    [Fact]
    public void AnnounceEditboxBlank()
    {
        var notes = new EditBox(App, "Notes");
        Assert.Equal(["Notes edit blank"], Render(Arrival(notes)));
    }

    [Fact]
    public void AnnounceListboxWithPosition()
    {
        var files = new ListBox(App, "Files", ["readme.txt", "b", "c"], numbered: true);
        Assert.Equal(["Files list readme.txt 1 of 3"], Render(Arrival(files)));
    }

    [Fact]
    public void AnnounceRolelessWidgetSkipsRole()
    {
        var arena = new CustomWidget(App, "Arena");
        Assert.Equal(["Arena"], Render(Arrival(arena)));
        arena.Description = "arrow keys move";
        Assert.Equal(["Arena arrow keys move"], Render(Arrival(arena)));
    }

    [Fact]
    public void AnnounceNamelessWidget()
    {
        var edit = new EditBox(App, null);
        Assert.Equal(["edit blank"], Render(Arrival(edit)));
    }

    [Fact]
    public void AnnounceDisabledRequired()
    {
        var name = new EditBox(App, "Name", "x") { Required = true };
        name.Disabled = true;
        Assert.Equal(["Name edit x unavailable required"], Render(Arrival(name)));
    }

    [Fact]
    public void AnnounceWithDescription()
    {
        var vol = new Slider(App, "Volume", 50, 0, 100) { Description = "master output" };
        Assert.Equal(["Volume slider 50 master output"], Render(Arrival(vol)));
    }

    [Fact]
    public void SpeakCharBasics()
    {
        Assert.Equal("a", SpeechRenderer.SpeakChar("a"));
        Assert.Equal("cap A", SpeechRenderer.SpeakChar("A"));
        Assert.Equal("space", SpeechRenderer.SpeakChar(" "));
        Assert.Equal("dot", SpeechRenderer.SpeakChar("."));
        Assert.Equal("new line", SpeechRenderer.SpeakChar("\n"));
        Assert.Equal("é", SpeechRenderer.SpeakChar("é"));
    }

    [Fact]
    public void RenderFocusedWithContext()
    {
        var ok = new Button(App, "OK");
        var events = Arrival(ok,
            new ContextEntry("Options", Role.Group), new ContextEntry("Confirm delete?", Role.Label));
        Assert.Equal(["Options group Confirm delete? OK button"], Render(events));
    }

    [Fact]
    public void RenderAnnounce()
    {
        Assert.Equal(["Nothing to delete"],
            Render([new AccessibilityEvent.Announce("Nothing to delete")]));
    }

    [Fact]
    public void RenderCheckedDelta()
    {
        var mute = new CheckBox(App, "Mute");
        Assert.Equal(["checked"],
            Render([new AccessibilityEvent.FieldValue(mute, Fields.Checked, true, FieldScope.Control)]));
        Assert.Equal(["not checked"],
            Render([new AccessibilityEvent.FieldValue(mute, Fields.Checked, false, FieldScope.Control)]));
    }

    [Fact]
    public void RenderEditNoop()
    {
        var notes = new EditBox(App, "Notes");
        Assert.Equal(["No text"], Render([new AccessibilityEvent.EditNoop(notes, EditNoopKind.NoText)]));
        Assert.Equal(["Nothing to select"], Render([new AccessibilityEvent.EditNoop(notes, EditNoopKind.NothingToSelect)]));
        Assert.Equal(["Nothing to delete"], Render([new AccessibilityEvent.EditNoop(notes, EditNoopKind.NothingToDelete)]));
        Assert.Equal(["Already selected to bottom, word"],
            Render([new AccessibilityEvent.EditNoop(notes, EditNoopKind.SelectedToBottom, "word")]));
        Assert.Equal(["Already selected to top, word"],
            Render([new AccessibilityEvent.EditNoop(notes, EditNoopKind.SelectedToTop, "word")]));
    }

    [Fact]
    public void RenderClipboard()
    {
        var notes = new EditBox(App, "Notes");
        Assert.Equal(["Copy"], Render([new AccessibilityEvent.Clipboard(notes, ClipboardOp.Copy)]));
    }

    [Fact]
    public void RenderSliderWithUnit()
    {
        var vol = new Slider(App, "Volume", 50, 0, 100, unit: "%");
        Assert.Equal(["50%"],
            Render([new AccessibilityEvent.FieldValue(vol, Fields.Number, 50.0, FieldScope.Control)]));
    }

    [Fact]
    public void RenderTabLandingSpeaksNameOnly()
    {
        var tabs = new TabControl(App, "Views", ["Files", "Playlist", "FX"]);
        tabs.ActiveIndex = 1;
        var item = tabs.CurrentItem!;
        Assert.Equal(["Playlist"], Render(
        [
            new AccessibilityEvent.ItemArrived(tabs, item),
            new AccessibilityEvent.FieldValue(tabs, Fields.Value, "Playlist", FieldScope.Item),
            new AccessibilityEvent.FieldValue(tabs, Fields.Checked, null, FieldScope.Item),
        ]));
    }

    [Fact]
    public void AControlDeltaBesideAnItemArrivalRendersAsADelta()
    {
        var files = new ListBox(App, "Files", ["a", "b"]);
        var item = files.CurrentItem!;
        // The landed item reads in full (a false state says nothing);
        // the control field that changed under it is news either way.
        Assert.Equal(["b available"], Render(
        [
            new AccessibilityEvent.FieldValue(files, Fields.Disabled, false, FieldScope.Control),
            new AccessibilityEvent.ItemArrived(files, item),
            new AccessibilityEvent.FieldValue(files, Fields.Value, "b", FieldScope.Item),
            new AccessibilityEvent.FieldValue(files, Fields.Checked, null, FieldScope.Item),
        ]));
    }

    [Fact]
    public void ABoundaryPrefixesTheReading()
    {
        var files = new ListBox(App, "Files", ["a"]);
        Assert.Equal(["top, a"], Render(
        [
            new AccessibilityEvent.BoundaryHit(files, Boundary.Top),
            new AccessibilityEvent.FieldValue(files, Fields.Value, "a", FieldScope.Item),
        ]));
    }

    [Fact]
    public void VerbosityTrimsRoleShortcutAndExtras()
    {
        var hand = new ListBox(App, "Hand", ["Ace of Spades", "b", "c", "d", "e", "f", "g", "h"], numbered: true)
        {
            KeyHelp = "Space selects.",
            Description = "Space selects.",
        };
        hand.AddShortcut(KeyCombo.WithAlt(Key.Char('h')));
        Assert.Equal(
            ["Hand list Ace of Spades 1 of 8 with help Space selects. alt h"],
            Render(Arrival(hand)));
        var quiet = new SpeechVerbosity { Roles = false, Shortcuts = false, Extras = false };
        Assert.Equal(["Hand Ace of Spades 1 of 8"], Render(Arrival(hand), quiet));
    }

    [Fact]
    public void RoleQualifiersGoWithTheRole()
    {
        var quiet = new SpeechVerbosity { Roles = false };
        var notes = new EditBox(App, "Notes", "hi", multiline: true) { ReadOnly = true };
        var picks = new ListBox(App, "Picks", ["a"], multiSelect: true);
        Assert.Equal(["Notes edit read only multi line hi"], Render(Arrival(notes)));
        Assert.Equal(["Notes hi"], Render(Arrival(notes), quiet));
        Assert.Equal(["Picks multi select list a"], Render(Arrival(picks)));
        Assert.Equal(["Picks a"], Render(Arrival(picks), quiet));
        // A read-only flip is a role word too: silent without roles.
        Assert.Empty(Render([new AccessibilityEvent.FieldValue(notes, Fields.ReadOnly, false, FieldScope.Control)], quiet));
    }

    [Fact]
    public void AFieldTheRoleImpliesIsNotSaidAgain()
    {
        var output = new Role("output", Fields.ReadOnly, Fields.Multiline);
        var log = new EditBox(App, "Log", "line one", multiline: true) { ReadOnly = true, Role = output };
        Assert.Equal(["Log output line one"], Render(Arrival(log)));
        // Present and readable all the same.
        Assert.True(log.Describe().Contains(Fields.ReadOnly));
    }

    [Fact]
    public void ARoleCanWordAFieldItsOwnWay()
    {
        var search = new Role("search");
        var renderer = new SpeechRenderer();
        renderer.SetRoleName(search, "list");
        renderer.Register(search, Fields.Filter, static (_, v) => v.Length == 0 ? "blank" : $"filter {v}");
        var box = new FilterListBox(App, "Search", ["a"]) { Role = search };
        var plain = new FilterListBox(App, "Commands", ["a"]);
        Assert.Equal(["Search list a 1 of 1 blank"], renderer.RenderTick(Arrival(box)));
        Assert.Equal(["Commands list a 1 of 1 no filter"], renderer.RenderTick(Arrival(plain)));
    }

    [Fact]
    public void VerbosityNeverTrimsActionableStates()
    {
        var quiet = new SpeechVerbosity { Roles = false, Shortcuts = false, Extras = false };
        var name = new FilterListBox(App, "Name", ["x"]) { Required = true, Warning = true };
        name.Disabled = true;
        Assert.Equal(["Name x 1 of 1 no filter unavailable required warning"], Render(Arrival(name), quiet));
    }

    [Fact]
    public void VerbosityGatesEchoesOfSuppressedParts()
    {
        var quiet = new SpeechVerbosity { Roles = false, Shortcuts = false, Extras = false };
        var save = new Button(App, "Save");
        // Echoes of parts the focus announcement suppressed stay silent;
        // a name change is never verbosity.
        Assert.Empty(Render([new AccessibilityEvent.FieldValue(save, Fields.WithHelp, true, FieldScope.Control)], quiet));
        Assert.Empty(Render([new AccessibilityEvent.FieldValue(save, Fields.Description, "new words", FieldScope.Control)], quiet));
        Assert.Equal(["Store"], Render([new AccessibilityEvent.FieldValue(save, Fields.Name, "Store", FieldScope.Control)], quiet));
        // The actionable state echoes survive full quiet.
        Assert.Equal(["unavailable"], Render([new AccessibilityEvent.FieldValue(save, Fields.Disabled, true, FieldScope.Control)], quiet));
    }

    [Fact]
    public void EchoGatesInsertionButNeverDeletion()
    {
        var box = new EditBox(App, "Notes");
        var ch = new AccessibilityEvent.Typing(box, "a", null, TypingKind.Insert);
        var sep = new AccessibilityEvent.Typing(box, " ", "hello", TypingKind.Insert);
        var del = new AccessibilityEvent.Typing(box, "a", null, TypingKind.Delete);
        var delWord = new AccessibilityEvent.Typing(box, "", "hello", TypingKind.DeleteWord);

        Assert.Equal(["a"], Render([ch]));
        Assert.Equal(["hello space"], Render([sep]));

        var chars = new SpeechVerbosity { Echo = TypingEcho.Characters };
        Assert.Equal(["a"], Render([ch], chars));
        Assert.Equal(["space"], Render([sep], chars));

        var words = new SpeechVerbosity { Echo = TypingEcho.Words };
        Assert.Empty(Render([ch], words));
        Assert.Equal(["hello"], Render([sep], words));

        var none = new SpeechVerbosity { Echo = TypingEcho.None };
        Assert.Empty(Render([ch], none));
        Assert.Empty(Render([sep], none));

        // Deletion is confirmation of a destruction, not typing
        // chatter: it survives every mode.
        Assert.Equal(["a"], Render([del], none));
        Assert.Equal(["hello"], Render([delWord], none));
    }

    [Fact]
    public void HarnessRendersUnderTheAppsLiveVerbosity()
    {
        using var ui = new TestApp(app => new EditBox(app, "Notes"));
        ui.App.SpeechVerbosity.Echo = TypingEcho.Words;
        ui.Type("hi ");
        ui.Expect("hi");
        ui.App.SpeechVerbosity.Echo = TypingEcho.None;
        ui.Type("go ");
        ui.ExpectNoSpeech();
        // Deletion speaks in every mode.
        ui.Press("backspace");
        ui.Expect("space");
    }
}
