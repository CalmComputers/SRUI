using Srui;
using Srui.Core;
using Xunit;

namespace Srui.Net.Tests;

public class SpeechRendererTests
{
    [Fact]
    public void AnnounceButtonWithShortcut()
    {
        var label = new WidgetLabel("Save", Role.Button);
        label.Shortcuts.Add(new WidgetShortcut(KeyCombo.WithAlt(Key.Char('s')), ShortcutAction.Jump));
        Assert.Equal("Save button alt s", SpeechRenderer.AnnounceFocus(label));

        // Only the first shortcut is announced.
        label.Shortcuts.Add(new WidgetShortcut(KeyCombo.WithCtrl(Key.Char('s')), ShortcutAction.Activate));
        Assert.Equal("Save button alt s", SpeechRenderer.AnnounceFocus(label));
    }

    [Fact]
    public void AnnounceCheckboxStates()
    {
        var label = new WidgetLabel("Word Wrap", Role.CheckBox) { Value = "not checked" };
        Assert.Equal("Word Wrap check box not checked", SpeechRenderer.AnnounceFocus(label));

        label.Value = "checked";
        Assert.Equal("Word Wrap check box checked", SpeechRenderer.AnnounceFocus(label));
    }

    [Fact]
    public void AnnounceEditboxBlank()
    {
        var label = new WidgetLabel("Notes", Role.Edit()) { Value = "blank" };
        Assert.Equal("Notes edit blank", SpeechRenderer.AnnounceFocus(label));
    }

    [Fact]
    public void AnnounceListboxWithPosition()
    {
        var label = new WidgetLabel("Files", Role.ListBox)
        {
            Value = "readme.txt",
            StateText = "1 of 3",
        };
        Assert.Equal("Files list readme.txt 1 of 3", SpeechRenderer.AnnounceFocus(label));
    }

    [Fact]
    public void AnnounceCustomWidgetSkipsRole()
    {
        var label = new WidgetLabel("Arena", Role.Custom);
        Assert.Equal("Arena", SpeechRenderer.AnnounceFocus(label));

        label.Description = "arrow keys move";
        Assert.Equal("Arena arrow keys move", SpeechRenderer.AnnounceFocus(label));
    }

    [Fact]
    public void AnnounceNamelessWidget()
    {
        var label = WidgetLabel.Nameless(Role.Edit());
        label.Value = "blank";
        Assert.Equal("edit blank", SpeechRenderer.AnnounceFocus(label));
    }

    [Fact]
    public void AnnounceDisabledRequired()
    {
        var label = new WidgetLabel("Name", Role.Edit())
        {
            States = States.Disabled | States.Required,
        };
        Assert.Equal("Name edit unavailable required", SpeechRenderer.AnnounceFocus(label));
    }

    [Fact]
    public void AnnounceWithDescription()
    {
        var label = new WidgetLabel("Volume", Role.Slider)
        {
            Value = "50",
            Description = "master output",
        };
        Assert.Equal("Volume slider 50 master output", SpeechRenderer.AnnounceFocus(label));
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
        var ev = new AccessibilityEvent.Focused(
            new NodeId(1), new WidgetLabel("Save", Role.Button), new List<string>());
        Assert.Equal("Save button", SpeechRenderer.RenderEvent(ev));
    }

    [Fact]
    public void RenderFocusedWithContext()
    {
        var ev = new AccessibilityEvent.Focused(
            new NodeId(1), new WidgetLabel("OK", Role.Button),
            new List<string> { "Confirm delete?" });
        Assert.Equal("Confirm delete? OK button", SpeechRenderer.RenderEvent(ev));
    }

    [Fact]
    public void RenderAnnounce()
    {
        var ev = new AccessibilityEvent.Announce("Nothing to delete");
        Assert.Equal("Nothing to delete", SpeechRenderer.RenderEvent(ev));
    }

    [Fact]
    public void RenderClipboard()
    {
        var ev = new AccessibilityEvent.Clipboard(new NodeId(1), ClipboardOp.Copy);
        Assert.Equal("Copy", SpeechRenderer.RenderEvent(ev));
    }

    [Fact]
    public void RenderSliderWithUnit()
    {
        var ev = new AccessibilityEvent.SliderChange(new NodeId(1), 50, "%");
        Assert.Equal("50%", SpeechRenderer.RenderEvent(ev));
    }

    [Fact]
    public void RenderTabChangeSpeaksNameOnly()
    {
        var ev = new AccessibilityEvent.TabChange(new NodeId(1), "Playlist", (1, 3));
        Assert.Equal("Playlist", SpeechRenderer.RenderEvent(ev));
    }
}

public class CoalesceTests
{
    private static CoreEvent FocusedEvent(ulong id, string name) =>
        new CoreEvent.Acc(new AccessibilityEvent.Focused(
            new NodeId(id), new WidgetLabel(name, Role.Button), new List<string>()));

    [Fact]
    public void CoalesceKeepsLastFocused()
    {
        var a = FocusedEvent(1, "A");
        var b = FocusedEvent(2, "B");
        var output = Coalesce.Apply(new List<CoreEvent> { a, b });
        Assert.Equal(new[] { b }, output);
    }

    [Fact]
    public void CoalesceKeepsAllAnnounces()
    {
        var one = new CoreEvent.Acc(new AccessibilityEvent.Announce("one"));
        var two = new CoreEvent.Acc(new AccessibilityEvent.Announce("two"));
        var output = Coalesce.Apply(new List<CoreEvent> { one, two });
        Assert.Equal(new CoreEvent[] { one, two }, output);
    }

    [Fact]
    public void CoalescePreservesWidgetEvents()
    {
        var a = FocusedEvent(1, "A");
        var b = FocusedEvent(2, "B");
        var w = new CoreEvent.Activated(new NodeId(3));
        var output = Coalesce.Apply(new List<CoreEvent> { a, w, b });
        Assert.Equal(new CoreEvent[] { w, b }, output);
    }

    [Fact]
    public void CoalesceIsPerKindNotGlobal()
    {
        var f = FocusedEvent(1, "A");
        var item = new CoreEvent.Acc(new AccessibilityEvent.ItemNav(
            new NodeId(2), "first", (0, 3), null));
        // Focused and ItemNav are different kinds — both survive.
        var output = Coalesce.Apply(new List<CoreEvent> { f, item });
        Assert.Equal(new CoreEvent[] { f, item }, output);
    }
}
