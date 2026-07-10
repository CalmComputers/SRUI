using Srui;
using Srui.Core;
using Xunit;

namespace Srui.Net.Tests;

public class SpeechRendererTests
{
    private static WidgetInfo Info(
        string? name, string role, string value = "", string stateText = "",
        WidgetStates states = WidgetStates.None, string description = "",
        params KeyCombo[] shortcuts) =>
        new(name, role, value, stateText, states, description, shortcuts);

    private static readonly SruiApp App = SruiApp.Headless();

    [Fact]
    public void AnnounceButtonWithShortcut()
    {
        var info = Info("Save", "button", shortcuts: KeyCombo.WithAlt(Key.Char('s')));
        Assert.Equal("Save button alt s", SpeechRenderer.AnnounceFocus(info));

        // Only the first shortcut is announced.
        var two = Info(
            "Save", "button",
            shortcuts: [KeyCombo.WithAlt(Key.Char('s')), KeyCombo.WithCtrl(Key.Char('s'))]);
        Assert.Equal("Save button alt s", SpeechRenderer.AnnounceFocus(two));
    }

    [Fact]
    public void AnnounceCheckboxStates()
    {
        Assert.Equal(
            "Word Wrap check box not checked",
            SpeechRenderer.AnnounceFocus(Info("Word Wrap", "check box", "not checked")));
        Assert.Equal(
            "Word Wrap check box checked",
            SpeechRenderer.AnnounceFocus(Info("Word Wrap", "check box", "checked")));
    }

    [Fact]
    public void AnnounceEditboxBlank()
    {
        Assert.Equal(
            "Notes edit blank",
            SpeechRenderer.AnnounceFocus(Info("Notes", "edit", "blank")));
    }

    [Fact]
    public void AnnounceListboxWithPosition()
    {
        Assert.Equal(
            "Files list readme.txt 1 of 3",
            SpeechRenderer.AnnounceFocus(Info("Files", "list", "readme.txt", "1 of 3")));
    }

    [Fact]
    public void AnnounceRolelessWidgetSkipsRole()
    {
        Assert.Equal("Arena", SpeechRenderer.AnnounceFocus(Info("Arena", "")));
        Assert.Equal(
            "Arena arrow keys move",
            SpeechRenderer.AnnounceFocus(Info("Arena", "", description: "arrow keys move")));
    }

    [Fact]
    public void AnnounceNamelessWidget()
    {
        Assert.Equal("edit blank", SpeechRenderer.AnnounceFocus(Info(null, "edit", "blank")));
    }

    [Fact]
    public void AnnounceDisabledRequired()
    {
        Assert.Equal(
            "Name edit unavailable required",
            SpeechRenderer.AnnounceFocus(
                Info("Name", "edit", states: WidgetStates.Disabled | WidgetStates.Required)));
    }

    [Fact]
    public void AnnounceWithDescription()
    {
        Assert.Equal(
            "Volume slider 50 master output",
            SpeechRenderer.AnnounceFocus(
                Info("Volume", "slider", "50", description: "master output")));
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
    public void RenderFocusedEvent()
    {
        var save = new Button(App, "Save");
        var ev = new AccessibilityEvent.Focused(save, Info("Save", "button"), []);
        Assert.Equal("Save button", SpeechRenderer.RenderEvent(ev));
    }

    [Fact]
    public void RenderFocusedWithContext()
    {
        var ok = new Button(App, "OK");
        var ev = new AccessibilityEvent.Focused(
            ok, Info("OK", "button"), ["Confirm delete?"]);
        Assert.Equal("Confirm delete? OK button", SpeechRenderer.RenderEvent(ev));
    }

    [Fact]
    public void RenderAnnounce()
    {
        var ev = new AccessibilityEvent.Announce("Nothing to delete");
        Assert.Equal("Nothing to delete", SpeechRenderer.RenderEvent(ev));
    }

    [Fact]
    public void RenderToggle()
    {
        var mute = new CheckBox(App, "Mute");
        Assert.Equal("checked",
            SpeechRenderer.RenderEvent(new AccessibilityEvent.Toggle(mute, true)));
        Assert.Equal("not checked",
            SpeechRenderer.RenderEvent(new AccessibilityEvent.Toggle(mute, false)));
    }

    [Fact]
    public void RenderEditNoop()
    {
        var notes = new EditBox(App, "Notes");
        Assert.Equal("No text", SpeechRenderer.RenderEvent(
            new AccessibilityEvent.EditNoop(notes, EditNoopKind.NoText)));
        Assert.Equal("Nothing to select", SpeechRenderer.RenderEvent(
            new AccessibilityEvent.EditNoop(notes, EditNoopKind.NothingToSelect)));
        Assert.Equal("Nothing to delete", SpeechRenderer.RenderEvent(
            new AccessibilityEvent.EditNoop(notes, EditNoopKind.NothingToDelete)));
        Assert.Equal("Already selected to bottom, word", SpeechRenderer.RenderEvent(
            new AccessibilityEvent.EditNoop(notes, EditNoopKind.SelectedToBottom, "word")));
        Assert.Equal("Already selected to top, word", SpeechRenderer.RenderEvent(
            new AccessibilityEvent.EditNoop(notes, EditNoopKind.SelectedToTop, "word")));
    }

    [Fact]
    public void RenderClipboard()
    {
        var notes = new EditBox(App, "Notes");
        var ev = new AccessibilityEvent.Clipboard(notes, ClipboardOp.Copy);
        Assert.Equal("Copy", SpeechRenderer.RenderEvent(ev));
    }

    [Fact]
    public void RenderSliderWithUnit()
    {
        var vol = new Slider(App, "Volume", 50, 0, 100, unit: "%");
        var ev = new AccessibilityEvent.SliderChange(vol, 50, "%");
        Assert.Equal("50%", SpeechRenderer.RenderEvent(ev));
    }

    [Fact]
    public void RenderTabChangeSpeaksNameOnly()
    {
        var tabs = new TabControl(App, "Views", ["Files", "Playlist", "FX"]);
        var ev = new AccessibilityEvent.TabChange(tabs, "Playlist", (1, 3));
        Assert.Equal("Playlist", SpeechRenderer.RenderEvent(ev));
    }
}

public class CoalesceTests
{
    private static readonly SruiApp App = SruiApp.Headless();

    private static CoreEvent FocusedEvent(string name)
    {
        var widget = new Button(App, name);
        return new CoreEvent.Acc(new AccessibilityEvent.Focused(
            widget,
            new WidgetInfo(name, "button", "", "", WidgetStates.None, "", []),
            []));
    }

    [Fact]
    public void CoalesceKeepsLastFocused()
    {
        var a = FocusedEvent("A");
        var b = FocusedEvent("B");
        var output = Coalesce.Apply([a, b]);
        Assert.Equal(new[] { b }, output);
    }

    [Fact]
    public void CoalesceKeepsAllAnnounces()
    {
        var one = new CoreEvent.Acc(new AccessibilityEvent.Announce("one"));
        var two = new CoreEvent.Acc(new AccessibilityEvent.Announce("two"));
        var output = Coalesce.Apply([one, two]);
        Assert.Equal(new CoreEvent[] { one, two }, output);
    }

    [Fact]
    public void CoalesceKeepsLastToggle()
    {
        var mute = new CheckBox(App, "Mute");
        var on = new CoreEvent.Acc(new AccessibilityEvent.Toggle(mute, true));
        var off = new CoreEvent.Acc(new AccessibilityEvent.Toggle(mute, false));
        var output = Coalesce.Apply([on, off]);
        Assert.Equal(new[] { off }, output);
    }

    [Fact]
    public void CoalescePreservesActivationsAndCallbacks()
    {
        var a = FocusedEvent("A");
        var b = FocusedEvent("B");
        var w = new CoreEvent.Activated(new NodeId(3));
        var output = Coalesce.Apply([a, w, b]);
        Assert.Equal(new CoreEvent[] { w, b }, output);
    }

    [Fact]
    public void CoalesceIsPerKindNotGlobal()
    {
        var f = FocusedEvent("A");
        var list = new ListBox(App, "L", ["first"]);
        var item = new CoreEvent.Acc(new AccessibilityEvent.ItemNav(list, "first", (0, 3), null));
        // Focused and ItemNav are different kinds — both survive (the
        // focus moves to the end of the batch).
        var output = Coalesce.Apply([f, item]);
        Assert.Equal(new CoreEvent[] { item, f }, output);
    }

    [Fact]
    public void CoalesceDeliversSettledFocusLast()
    {
        // What happened is spoken before where you are: an announcement
        // emitted after a focus change still precedes it in the batch.
        var f = FocusedEvent("A");
        var announce = new CoreEvent.Acc(new AccessibilityEvent.Announce("Created."));
        var output = Coalesce.Apply([f, announce]);
        Assert.Equal(new CoreEvent[] { announce, f }, output);
    }
}
