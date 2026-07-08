using Srui;
using Srui.Core;
using Xunit;

namespace Srui.Net.Tests;

public class EditBoxCoreTests
{
    private static readonly NodeId Node = new(1);

    private static List<string> Speech(EditBoxCore.Result result) =>
        result.Events
            .Select(SpeechRenderer.RenderEvent)
            .Where(s => s is not null)
            .Select(s => s!)
            .ToList();

    private static EditBoxCore.Result Handle(in InputEvent input, EditorState editor) =>
        EditBoxCore.Handle(Node, input, editor, new NoClipboard());

    private static InputEvent Simple(InputKind kind) => InputEvent.Simple(kind);

    [Fact]
    public void TypeChar()
    {
        var editor = new EditorState("", false);
        var result = Handle(InputEvent.TypeChar('h'), editor);
        Assert.Equal(new[] { "h" }, Speech(result));
        Assert.True(result.Changed);
        Assert.Equal("h", editor.Text());
    }

    [Fact]
    public void RightArrowMidText()
    {
        var editor = new EditorState("abc", false) { Cursor = 0 };
        var result = Handle(Simple(InputKind.MoveRight), editor);
        Assert.Equal(new[] { "b" }, Speech(result));
        Assert.Equal(1, editor.Cursor);
        Assert.False(result.Changed);
    }

    [Fact]
    public void LeftAtStartBoundary()
    {
        var editor = new EditorState("az", false) { Cursor = 0 };
        var result = Handle(Simple(InputKind.MoveLeft), editor);
        Assert.Equal(new[] { "Top, a" }, Speech(result));
    }

    [Fact]
    public void RightToEndSpeaksBottom()
    {
        var editor = new EditorState("ab", false) { Cursor = 1 };
        var result = Handle(Simple(InputKind.MoveRight), editor);
        Assert.Equal(new[] { "Bottom, b" }, Speech(result));
    }

    [Fact]
    public void EnterMultiline()
    {
        var editor = new EditorState("", true);
        var result = Handle(Simple(InputKind.Activate), editor);
        Assert.Equal(new[] { "new line" }, Speech(result));
        Assert.True(result.Changed);
    }

    [Fact]
    public void EnterSinglelineNotConsumed()
    {
        var editor = new EditorState("", false);
        var result = Handle(Simple(InputKind.Activate), editor);
        Assert.False(result.Consumed);
    }

    [Fact]
    public void DeleteBackward()
    {
        var editor = new EditorState("ab", false) { Cursor = 2 };
        var result = Handle(Simple(InputKind.DeleteBackward), editor);
        Assert.True(result.Changed);
        Assert.Equal("a", editor.Text());
    }

    [Fact]
    public void EndSingleLineNoBottomFirstPress()
    {
        var editor = new EditorState("hello", false) { Cursor = 0 };
        var result = Handle(Simple(InputKind.MoveToLineEnd), editor);
        Assert.Equal(5, editor.Cursor);
        Assert.DoesNotContain("Bottom", Speech(result)[0]);
    }

    [Fact]
    public void EndSingleLineBottomOnRepeat()
    {
        var editor = new EditorState("hello", false) { Cursor = 5 };
        var result = Handle(Simple(InputKind.MoveToLineEnd), editor);
        Assert.StartsWith("Bottom", Speech(result)[0]);
    }

    [Fact]
    public void ReadOnlyTypingIsSilent()
    {
        var editor = new EditorState("hello", false) { ReadOnly = true };
        var result = Handle(InputEvent.TypeChar('x'), editor);
        Assert.True(result.Consumed);
        Assert.False(result.Changed);
        Assert.Empty(result.Events);
        Assert.Equal("hello", editor.Text());
    }

    [Fact]
    public void ReadOnlyMultilineEnterFallsThrough()
    {
        var editor = new EditorState("hello", true) { ReadOnly = true };
        var result = Handle(Simple(InputKind.Activate), editor);
        Assert.False(result.Consumed);
        Assert.Equal("hello", editor.Text());
    }

    [Fact]
    public void UnhandledInputIgnored()
    {
        var editor = new EditorState("abc", false);
        var result = Handle(Simple(InputKind.SpeakFocus), editor);
        Assert.Empty(result.Events);
        Assert.False(result.Changed);
    }

    [Fact]
    public void LabelValueEmpty()
    {
        var editor = new EditorState("", false);
        Assert.Equal("blank", EditBoxCore.LabelValue(editor));
    }

    [Fact]
    public void LabelValueCurrentLine()
    {
        var editor = new EditorState("hello world\nsecond line", true);
        Assert.Equal("hello world", EditBoxCore.LabelValue(editor));
        editor.Cursor = 12;
        Assert.Equal("second line", EditBoxCore.LabelValue(editor));
    }

    [Fact]
    public void LabelValueSelection()
    {
        var editor = new EditorState("hello world", false) { Selection = (0, 5), Cursor = 5 };
        Assert.Equal("selected hello", EditBoxCore.LabelValue(editor));
    }

    [Fact]
    public void LabelValueSingleLine()
    {
        var editor = new EditorState("hello world", false);
        Assert.Equal("hello world", EditBoxCore.LabelValue(editor));
    }

    // ── Typing events ──

    [Fact]
    public void TypeCharWordBoundarySpeaksWordThenChar()
    {
        var editor = new EditorState("hello", false) { Cursor = 5 };
        var result = Handle(InputEvent.TypeChar(' '), editor);
        Assert.Equal(new[] { "hello space" }, Speech(result));
    }

    [Fact]
    public void TypeCharMidWordNoWordPayload()
    {
        var editor = new EditorState("hel", false) { Cursor = 3 };
        var result = Handle(InputEvent.TypeChar('l'), editor);
        Assert.Equal(new[] { "l" }, Speech(result));
        var typing = Assert.IsType<AccessibilityEvent.Typing>(result.Events[0]);
        Assert.Null(typing.LastWord);
    }

    [Fact]
    public void EnterAfterWordSpeaksWordThenNewline()
    {
        var editor = new EditorState("hello", true) { Cursor = 5 };
        var result = Handle(Simple(InputKind.Activate), editor);
        Assert.Equal(new[] { "hello new line" }, Speech(result));
    }

    [Fact]
    public void FirstSeparatorOnlyInRun()
    {
        var editor = new EditorState("hello", false) { Cursor = 5 };

        var result = Handle(InputEvent.TypeChar('.'), editor);
        Assert.Equal(new[] { "hello dot" }, Speech(result));

        result = Handle(InputEvent.TypeChar('.'), editor);
        Assert.Equal(new[] { "dot" }, Speech(result));

        result = Handle(InputEvent.TypeChar('.'), editor);
        Assert.Equal(new[] { "dot" }, Speech(result));

        result = Handle(InputEvent.TypeChar(' '), editor);
        Assert.Equal(new[] { "space" }, Speech(result));

        foreach (var ch in "world")
            Handle(InputEvent.TypeChar(ch), editor);
        result = Handle(InputEvent.TypeChar('!'), editor);
        Assert.Equal(new[] { "world bang" }, Speech(result));
    }

    [Fact]
    public void EnterAfterSpaceDoesntRepeatWord()
    {
        var editor = new EditorState("hello ", true) { Cursor = 6 };
        var result = Handle(Simple(InputKind.Activate), editor);
        Assert.Equal(new[] { "new line" }, Speech(result));
    }

    [Fact]
    public void TypeOverSelectionEmitsClearThenTyping()
    {
        var editor = new EditorState("hi", false) { Selection = (0, 2), Cursor = 2 };
        var result = Handle(InputEvent.TypeChar('x'), editor);
        Assert.Equal(new[] { "Selection removed", "x" }, Speech(result));
        var selection = Assert.IsType<AccessibilityEvent.Selection>(result.Events[0]);
        Assert.Equal(SelectionKind.Cleared, selection.Kind);
        var typing = Assert.IsType<AccessibilityEvent.Typing>(result.Events[1]);
        Assert.Equal(TypingKind.Insert, typing.Kind);
    }

    [Fact]
    public void DeleteWithSelectionEmitsOnlyClear()
    {
        var editor = new EditorState("hi", false) { Selection = (0, 2), Cursor = 2 };
        var result = Handle(Simple(InputKind.DeleteBackward), editor);
        Assert.Equal(new[] { "Selection removed" }, Speech(result));
        Assert.Equal("", editor.Text());
    }

    [Fact]
    public void DeleteWordEmitsTypingDeleteWord()
    {
        var editor = new EditorState("hello world", false) { Cursor = 11 };
        var result = Handle(Simple(InputKind.DeleteWordBackward), editor);
        var typing = Assert.IsType<AccessibilityEvent.Typing>(result.Events[0]);
        Assert.Equal(TypingKind.DeleteWord, typing.Kind);
        Assert.Equal("", typing.Grapheme);
        Assert.NotNull(typing.LastWord);
    }

    [Fact]
    public void CopyEmitsClipboardEvent()
    {
        var editor = new EditorState("hello", false) { Selection = (0, 5), Cursor = 5 };
        var result = Handle(Simple(InputKind.Copy), editor);
        Assert.Equal(new[] { "Copy" }, Speech(result));
        var clip = Assert.IsType<AccessibilityEvent.Clipboard>(result.Events[0]);
        Assert.Equal(ClipboardOp.Copy, clip.Op);
    }

    [Fact]
    public void SelectAllEmitsSelectionAll()
    {
        var editor = new EditorState("hello", false);
        var result = Handle(Simple(InputKind.SelectAll), editor);
        Assert.Equal(new[] { "hello selected" }, Speech(result));
        var selection = Assert.IsType<AccessibilityEvent.Selection>(result.Events[0]);
        Assert.Equal(SelectionKind.All, selection.Kind);
    }

    [Fact]
    public void SelectionDeltaSpeech()
    {
        var editor = new EditorState("hello", false) { Cursor = 0 };
        var result = Handle(Simple(InputKind.SelectRight), editor);
        Assert.Equal(new[] { "h selected" }, Speech(result));
        result = Handle(Simple(InputKind.SelectRight), editor);
        Assert.Equal(new[] { "e selected" }, Speech(result));
        // Shrink the selection from the cursor side.
        result = Handle(Simple(InputKind.SelectLeft), editor);
        Assert.Equal(new[] { "e unselected" }, Speech(result));
    }
}
