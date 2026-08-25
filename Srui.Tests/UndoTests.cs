using Srui;
using Srui.Core;
using Srui.Testing;
using Xunit;

namespace Srui.Tests;

/// <summary>Editor undo: unit boundaries (a typing sequence ends only
/// when the cursor is elsewhere at the next edit or ten seconds pass
/// without one; bulk operations take units of their own), the
/// restored-state announcement, redo, and the memory budget.</summary>
public class UndoTests
{
    private static readonly Widget Node = new CustomWidget(SruiApp.Headless(), "editor");

    private sealed class FakeClipboard : IClipboard
    {
        public string? Content;
        public string? Read() => Content;
        public void Write(string text) => Content = text;
    }

    private static List<string> Speech(EditBoxCore.Result result) =>
        result.Events
            .Select(SpeechRenderer.RenderEvent)
            .Where(s => s is not null)
            .Select(s => s!)
            .ToList();

    private static EditBoxCore.Result Handle(
        in InputEvent input, EditorState editor, ulong now = 0, IClipboard? clipboard = null) =>
        EditBoxCore.Handle(Node, input, editor, clipboard ?? new NoClipboard(), now);

    private static void Type(EditorState editor, string text, ulong now = 0)
    {
        foreach (var ch in text)
            Handle(InputEvent.TypeChar(ch), editor, now);
    }

    private static EditBoxCore.Result Undo(EditorState editor) =>
        Handle(InputEvent.Simple(InputKind.Undo), editor);

    private static EditBoxCore.Result Redo(EditorState editor) =>
        Handle(InputEvent.Simple(InputKind.Redo), editor);

    // ── Nothing to act on ──

    [Fact]
    public void EmptyHistorySpeaksNothingToUndo()
    {
        var editor = new EditorState("hello", false);
        var result = Undo(editor);
        Assert.True(result.Consumed);
        Assert.False(result.Changed);
        Assert.Equal(new[] { "Nothing to undo" }, Speech(result));
        Assert.Equal("hello", editor.Text());
    }

    [Fact]
    public void EmptyRedoSpeaksNothingToRedo()
    {
        var editor = new EditorState("", false);
        Type(editor, "ab");
        var result = Redo(editor);
        Assert.Equal(new[] { "Nothing to redo" }, Speech(result));
    }

    [Fact]
    public void ReadOnlySwallowsUndoSilently()
    {
        var editor = new EditorState("hello", false) { ReadOnly = true };
        var result = Undo(editor);
        Assert.True(result.Consumed);
        Assert.Empty(result.Events);
    }

    // ── Typing sequences ──

    [Fact]
    public void TypingBurstIsOneUnit()
    {
        var editor = new EditorState("", false);
        Type(editor, "hello world");
        var result = Undo(editor);
        Assert.True(result.Changed);
        Assert.Equal(new[] { "blank" }, Speech(result));
        Assert.Equal("", editor.Text());
        Assert.Equal(0, editor.Cursor);
    }

    [Fact]
    public void BackspaceRidesTheSequence()
    {
        var editor = new EditorState("", false);
        Type(editor, "helol");
        Handle(InputEvent.Simple(InputKind.DeleteBackward), editor);
        Handle(InputEvent.Simple(InputKind.DeleteBackward), editor);
        Type(editor, "lo");
        Assert.Equal("hello", editor.Text());
        Undo(editor);
        Assert.Equal("", editor.Text());
        Assert.Equal(new[] { "Nothing to undo" }, Speech(Undo(editor)));
    }

    [Fact]
    public void DeleteForwardRidesTheSequence()
    {
        var editor = new EditorState("abc", false) { Cursor = 0 };
        Handle(InputEvent.Simple(InputKind.DeleteForward), editor);
        Handle(InputEvent.Simple(InputKind.DeleteForward), editor);
        Assert.Equal("c", editor.Text());
        Undo(editor);
        Assert.Equal("abc", editor.Text());
        Assert.Equal(0, editor.Cursor);
    }

    [Fact]
    public void NewlineRidesTheSequence()
    {
        var editor = new EditorState("", true);
        Type(editor, "ab");
        Handle(InputEvent.Simple(InputKind.Activate), editor);
        Type(editor, "cd");
        Assert.Equal("ab\ncd", editor.Text());
        Undo(editor);
        Assert.Equal("", editor.Text());
    }

    [Fact]
    public void MoveAwayThenTypeStartsNewUnit()
    {
        var editor = new EditorState("", false);
        Type(editor, "ab");
        Handle(InputEvent.Simple(InputKind.MoveLeft), editor);
        Type(editor, "x");
        Assert.Equal("axb", editor.Text());
        var result = Undo(editor);
        Assert.Equal("ab", editor.Text());
        Assert.Equal(1, editor.Cursor);
        Assert.Equal(new[] { "ab" }, Speech(result));
        Undo(editor);
        Assert.Equal("", editor.Text());
    }

    [Fact]
    public void MoveAwayAndBackContinuesTheSequence()
    {
        var editor = new EditorState("", false);
        Type(editor, "ab");
        Handle(InputEvent.Simple(InputKind.MoveLeft), editor);
        Handle(InputEvent.Simple(InputKind.MoveRight), editor);
        Type(editor, "c");
        Undo(editor);
        Assert.Equal("", editor.Text());
    }

    [Fact]
    public void TenSecondGapStartsNewUnit()
    {
        var editor = new EditorState("", false);
        Type(editor, "a", now: 0);
        Type(editor, "b", now: 5_000);
        Type(editor, "cd", now: 15_001);
        Assert.Equal("abcd", editor.Text());
        Undo(editor);
        Assert.Equal("ab", editor.Text());
        Undo(editor);
        Assert.Equal("", editor.Text());
    }

    [Fact]
    public void GapWithinTenSecondsMerges()
    {
        var editor = new EditorState("", false);
        Type(editor, "ab", now: 0);
        Type(editor, "cd", now: 10_000);
        Undo(editor);
        Assert.Equal("", editor.Text());
    }

    // ── Bulk operations ──

    [Fact]
    public void SelectionDeleteIsOwnUnitAndComesBackSelected()
    {
        var editor = new EditorState("hello world", false) { Selection = (0, 5), Cursor = 5 };
        Handle(InputEvent.Simple(InputKind.DeleteBackward), editor);
        Assert.Equal(" world", editor.Text());
        var result = Undo(editor);
        Assert.Equal("hello world", editor.Text());
        Assert.Equal((0, 5), editor.Selection);
        Assert.Equal(new[] { "selected hello" }, Speech(result));
    }

    [Fact]
    public void TypeOverSelectionIsOneUnit()
    {
        var editor = new EditorState("hi", false) { Selection = (0, 2), Cursor = 2 };
        Handle(InputEvent.TypeChar('x'), editor);
        Assert.Equal("x", editor.Text());
        var result = Undo(editor);
        Assert.Equal("hi", editor.Text());
        Assert.Equal((0, 2), editor.Selection);
        Assert.Equal(new[] { "selected hi" }, Speech(result));
    }

    [Fact]
    public void TypeOverSelectionEndsTheSequenceBothSides()
    {
        var editor = new EditorState("", false);
        Type(editor, "ab");
        editor.Selection = (0, 2);
        editor.Cursor = 2;
        Handle(InputEvent.TypeChar('x'), editor);
        Type(editor, "y");
        Assert.Equal("xy", editor.Text());
        // Three units: the burst, the replace, the trailing typing.
        Undo(editor);
        Assert.Equal("x", editor.Text());
        Undo(editor);
        Assert.Equal("ab", editor.Text());
        Undo(editor);
        Assert.Equal("", editor.Text());
    }

    [Fact]
    public void WordDeleteIsOwnUnit()
    {
        var editor = new EditorState("", false);
        Type(editor, "hello world");
        Handle(InputEvent.Simple(InputKind.DeleteWordBackward), editor);
        Assert.Equal("hello ", editor.Text());
        var result = Undo(editor);
        Assert.Equal("hello world", editor.Text());
        Assert.Equal(new[] { "hello world" }, Speech(result));
        Undo(editor);
        Assert.Equal("", editor.Text());
    }

    [Fact]
    public void PasteSplitsTheSequence()
    {
        var editor = new EditorState("", false);
        var clipboard = new FakeClipboard { Content = "XY" };
        Type(editor, "ab");
        Handle(InputEvent.Simple(InputKind.Paste), editor, clipboard: clipboard);
        Type(editor, "cd");
        Assert.Equal("abXYcd", editor.Text());
        Undo(editor);
        Assert.Equal("abXY", editor.Text());
        Undo(editor);
        Assert.Equal("ab", editor.Text());
        Undo(editor);
        Assert.Equal("", editor.Text());
    }

    [Fact]
    public void CutUndoRestoresTextAndSelection()
    {
        var editor = new EditorState("hello", false) { Selection = (1, 4), Cursor = 4 };
        var clipboard = new FakeClipboard();
        Handle(InputEvent.Simple(InputKind.Cut), editor, clipboard: clipboard);
        Assert.Equal("ho", editor.Text());
        Assert.Equal("ell", clipboard.Content);
        var result = Undo(editor);
        Assert.Equal("hello", editor.Text());
        Assert.Equal((1, 4), editor.Selection);
        Assert.Equal(new[] { "selected ell" }, Speech(result));
        // Undo restores the document, never the clipboard.
        Assert.Equal("ell", clipboard.Content);
    }

    // ── Redo ──

    [Fact]
    public void RedoReappliesAndRestoresAfterState()
    {
        var editor = new EditorState("", false);
        Type(editor, "hi");
        Undo(editor);
        var result = Redo(editor);
        Assert.Equal("hi", editor.Text());
        Assert.Equal(2, editor.Cursor);
        Assert.Equal(new[] { "hi" }, Speech(result));
    }

    [Fact]
    public void RedoWalksBackThroughUnits()
    {
        var editor = new EditorState("", false);
        Type(editor, "ab", now: 0);
        Type(editor, "cd", now: 20_000);
        Undo(editor);
        Undo(editor);
        Assert.Equal("", editor.Text());
        Redo(editor);
        Assert.Equal("ab", editor.Text());
        Redo(editor);
        Assert.Equal("abcd", editor.Text());
        Assert.Equal(new[] { "Nothing to redo" }, Speech(Redo(editor)));
    }

    [Fact]
    public void NewEditClearsRedo()
    {
        var editor = new EditorState("", false);
        Type(editor, "ab");
        Undo(editor);
        Type(editor, "x");
        Assert.Equal(new[] { "Nothing to redo" }, Speech(Redo(editor)));
        Assert.Equal("x", editor.Text());
    }

    [Fact]
    public void TypingAfterUndoNeverMergesIntoRedoneUnit()
    {
        var editor = new EditorState("", false);
        Type(editor, "ab");
        Undo(editor);
        Redo(editor);
        // The redone unit is sealed: new typing is its own unit.
        Type(editor, "cd");
        Undo(editor);
        Assert.Equal("ab", editor.Text());
    }

    // ── Memory budget ──

    [Fact]
    public void OldestUnitsAreEvictedPastTheBudget()
    {
        var editor = new EditorState("", false);
        editor.History.MaxChars = 4;
        Type(editor, "ab", now: 0);
        Type(editor, "cd", now: 20_000);
        Type(editor, "ef", now: 40_000);
        Undo(editor);
        Assert.Equal("abcd", editor.Text());
        Undo(editor);
        Assert.Equal("ab", editor.Text());
        // The first unit was evicted; its edit is permanent.
        Assert.Equal(new[] { "Nothing to undo" }, Speech(Undo(editor)));
        Assert.Equal("ab", editor.Text());
    }

    [Fact]
    public void NewestUnitIsKeptEvenOverBudget()
    {
        var editor = new EditorState("", false);
        editor.History.MaxChars = 0;
        Type(editor, "abc");
        Undo(editor);
        Assert.Equal("", editor.Text());
    }

    // ── The widget surface ──

    [Fact]
    public void CtrlZUndoesAndSpeaksTheRestoredLine()
    {
        var ui = new TestApp();
        var notes = new EditBox(ui.App, "Notes");
        notes.Focus();
        ui.Type("ab");
        ui.Wait(11_000);
        ui.Type("cd");
        ui.Press("ctrl+z");
        Assert.Equal("ab", notes.Text);
        Assert.Equal(new[] { "ab" }, ui.Spoken());
        ui.Press("ctrl+z");
        Assert.Equal("", notes.Text);
        Assert.Equal(new[] { "blank" }, ui.Spoken());
        ui.Press("ctrl+z");
        Assert.Equal(new[] { "Nothing to undo" }, ui.Spoken());
    }

    [Fact]
    public void CtrlYAndCtrlShiftZBothRedo()
    {
        var ui = new TestApp();
        var notes = new EditBox(ui.App, "Notes");
        notes.Focus();
        ui.Type("ab");
        ui.Wait(11_000);
        ui.Type("cd");
        ui.Press("ctrl+z");
        ui.Press("ctrl+z");
        ui.Press("ctrl+y");
        Assert.Equal("ab", notes.Text);
        ui.Press("ctrl+shift+z");
        Assert.Equal("abcd", notes.Text);
        Assert.Equal(new[] { "abcd" }, ui.Spoken());
    }

    [Fact]
    public void UndoRaisesChanged()
    {
        var ui = new TestApp();
        var notes = new EditBox(ui.App, "Notes");
        notes.Focus();
        ui.Type("ab");
        var changed = 0;
        notes.Changed += () => changed++;
        ui.Press("ctrl+z");
        Assert.Equal(1, changed);
    }

    [Fact]
    public void InsertTextIsOneUndoUnit()
    {
        var ui = new TestApp();
        var notes = new EditBox(ui.App, "Notes");
        notes.InsertText("hello");
        notes.Undo();
        Assert.Equal("", notes.Text);
        notes.Redo();
        Assert.Equal("hello", notes.Text);
    }

    [Fact]
    public void ReplaceRangeIsOneUndoUnit()
    {
        var ui = new TestApp();
        var notes = new EditBox(ui.App, "Notes", "hello world");
        notes.ReplaceRange(0, 5, "goodbye");
        Assert.Equal("goodbye world", notes.Text);
        notes.Undo();
        Assert.Equal("hello world", notes.Text);
    }

    [Fact]
    public void TextSetterClearsHistory()
    {
        var ui = new TestApp();
        var notes = new EditBox(ui.App, "Notes");
        notes.Focus();
        ui.Type("ab");
        notes.Text = "loaded";
        ui.Drain();
        ui.Press("ctrl+z");
        Assert.Equal(new[] { "Nothing to undo" }, ui.Spoken());
        Assert.Equal("loaded", notes.Text);
    }

    [Fact]
    public void UnfocusedProgrammaticUndoIsSilent()
    {
        var ui = new TestApp();
        var notes = new EditBox(ui.App, "Notes");
        var other = new Button(ui.App, "Other");
        notes.InsertText("hello");
        other.Focus();
        ui.Drain();
        notes.Undo();
        Assert.Equal("", notes.Text);
        Assert.Empty(ui.Spoken());
    }

    [Fact]
    public void UndoRestoresSelectionReplacedOnFocusEntry()
    {
        // Single-line boxes select all on focus; typing replaces the
        // seed — one bulk unit whose undo brings the seed back selected.
        var ui = new TestApp();
        var name = new EditBox(ui.App, "Name", "draft");
        name.Focus();
        ui.Type("x");
        Assert.Equal("x", name.Text);
        ui.Press("ctrl+z");
        Assert.Equal("draft", name.Text);
        Assert.Equal(new[] { "selected draft" }, ui.Spoken());
    }

    [Fact]
    public void EditBoxReservesUndoCombos()
    {
        var ui = new TestApp();
        var notes = new EditBox(ui.App, "Notes");
        Assert.True(notes.ReservesKey(KeyCombo.WithCtrl(Key.Char('z'))));
        Assert.True(notes.ReservesKey(KeyCombo.WithCtrl(Key.Char('y'))));
        Assert.True(notes.ReservesKey(KeyCombo.CtrlShift(Key.Char('z'))));
        Assert.False(notes.ReservesKey(KeyCombo.WithAlt(Key.Char('z'))));
    }
}
