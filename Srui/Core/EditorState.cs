namespace Srui.Core;

/// <summary>Persistent state for a text editor widget: rope content,
/// cursor, selection, and the sticky column for vertical navigation.
/// Every mutation and movement returns its speech feedback. All positions
/// are UTF-16 code-unit indices.</summary>
internal sealed class EditorState
{
    public Rope Rope;
    /// <summary>Cursor position.</summary>
    public int Cursor;
    /// <summary>Selection as (anchor, cursor). Anchor is the fixed end.</summary>
    public (int Anchor, int Cursor)? Selection;
    /// <summary>Sticky column for vertical line navigation (offset from
    /// line start).</summary>
    public int? PreferredColumn;
    public bool Multiline;
    public bool ReadOnly;

    /// <summary>Undo history. Recording rides <see cref="Splice"/>, so
    /// every mutation path participates; <see cref="SetText"/> clears it.</summary>
    public readonly UndoHistory History = new();

    /// <summary>Operation-scope nesting (a selection delete inside an
    /// insert); only the outermost scope reaches the history.</summary>
    private int _opDepth;

    public EditorState(string text, bool multiline)
    {
        Rope = new Rope(text);
        Multiline = multiline;
    }

    // ── The splice chokepoint ──

    private void BeginOp(bool bulk)
    {
        if (_opDepth++ == 0)
            History.BeginOp(Cursor, Selection, bulk);
    }

    private void EndOp()
    {
        if (--_opDepth == 0)
            History.EndOp(Cursor, Selection);
    }

    /// <summary>Replace [start, end) with text — the single rope
    /// mutation point for edits, recording into the history. Must run
    /// inside an operation scope (the history throws otherwise).
    /// Returns the removed text.</summary>
    private string Splice(int start, int end, string text)
    {
        if (start == end && text.Length == 0)
            return "";
        var removed = start == end ? "" : Rope.Substring(start, end);
        if (start != end)
            Rope.Remove(start, end);
        if (text.Length != 0)
            Rope.Insert(start, text);
        History.RecordSplice(start, removed, text);
        return removed;
    }

    /// <summary>Undo the most recent unit: reverse its splices and
    /// restore the cursor and selection from before it — a selection
    /// delete comes back selected. False with nothing to undo.</summary>
    public bool Undo()
    {
        if (History.PopUndo() is not UndoHistory.Unit unit)
            return false;
        for (var i = unit.Splices.Count - 1; i >= 0; i--)
        {
            var splice = unit.Splices[i];
            if (splice.Inserted.Length != 0)
                Rope.Remove(splice.Start, splice.Start + splice.Inserted.Length);
            if (splice.Removed.Length != 0)
                Rope.Insert(splice.Start, splice.Removed);
        }
        Cursor = unit.CursorBefore;
        Selection = unit.SelectionBefore;
        PreferredColumn = null;
        return true;
    }

    /// <summary>Reapply the most recently undone unit, restoring the
    /// cursor and selection from after it. False with nothing to redo.</summary>
    public bool Redo()
    {
        if (History.PopRedo() is not UndoHistory.Unit unit)
            return false;
        foreach (var splice in unit.Splices)
        {
            if (splice.Removed.Length != 0)
                Rope.Remove(splice.Start, splice.Start + splice.Removed.Length);
            if (splice.Inserted.Length != 0)
                Rope.Insert(splice.Start, splice.Inserted);
        }
        Cursor = unit.CursorAfter;
        Selection = unit.SelectionAfter;
        PreferredColumn = null;
        return true;
    }

    /// <summary>Current content as a string — O(n).</summary>
    public string Text() => Rope.ToString();

    public int Length => Rope.Length;

    public bool IsEmpty => Rope.Length == 0;

    /// <summary>Whether a selection is active (non-empty range).</summary>
    public bool HasSelection => Selection is (var a, var c) && a != c;

    /// <summary>Collapse an active selection directionally: backward
    /// collapses to the start, forward to the end. True if there was a
    /// selection to collapse.</summary>
    public bool CollapseSelectionDirectional(bool forward)
    {
        if (Selection is (var anchor, var cursor))
        {
            Selection = null;
            if (anchor != cursor)
            {
                Cursor = forward ? Math.Max(anchor, cursor) : Math.Min(anchor, cursor);
                PreferredColumn = null;
                return true;
            }
        }
        return false;
    }

    /// <summary>All text, or "blank" when empty.</summary>
    public string ReadAll() => IsEmpty ? "blank" : Text();

    // ── Editing operations ──

    /// <summary>Insert a character at the cursor. Returns speech feedback.</summary>
    public string InsertChar(char ch) => InsertRuneText(ch.ToString());

    /// <summary>Insert one character given as its UTF-16 form (one or two
    /// units — astral characters are two).</summary>
    public string InsertRuneText(string s)
    {
        if (ReadOnly)
            return "";
        BeginOp(bulk: HasSelection);
        try
        {
            var hadSelection = DeleteSelectionSilent();
            Splice(Cursor, Cursor, s);
            Cursor += s.Length;
            Selection = null;
            PreferredColumn = null;
            var charSpeech = SpeechRenderer.SpeakChar(s);
            return hadSelection ? $"selection removed, {charSpeech}" : charSpeech;
        }
        finally
        {
            EndOp();
        }
    }

    /// <summary>Insert a newline at the cursor (multiline only).</summary>
    public string InsertNewline()
    {
        if (!Multiline || ReadOnly)
            return "";
        BeginOp(bulk: HasSelection);
        try
        {
            var hadSelection = DeleteSelectionSilent();
            Splice(Cursor, Cursor, "\n");
            Cursor += 1;
            Selection = null;
            PreferredColumn = null;
            var speech = SpeechRenderer.SpeakChar("\n");
            return hadSelection ? $"selection removed, {speech}" : speech;
        }
        finally
        {
            EndOp();
        }
    }

    /// <summary>Delete the grapheme before the cursor. Returns speech for
    /// the deleted character, or null when there was nothing to delete.</summary>
    public string? DeleteBackward()
    {
        if (ReadOnly)
            return null;
        BeginOp(bulk: HasSelection);
        try
        {
            if (DeleteSelectionSilent())
                return "deleted";
            if (Cursor == 0)
                return null;
            if (TextNav.PrevGrapheme(Rope, Cursor) is not int prev)
                return null;
            if (TextNav.GraphemeAt(Rope, prev) is not string deleted)
                return null;
            Splice(prev, Cursor, "");
            Cursor = prev;
            PreferredColumn = null;
            return SpeechRenderer.SpeakChar(deleted);
        }
        finally
        {
            EndOp();
        }
    }

    /// <summary>Delete the grapheme after the cursor.</summary>
    public string? DeleteForward()
    {
        if (ReadOnly)
            return null;
        BeginOp(bulk: HasSelection);
        try
        {
            if (DeleteSelectionSilent())
                return "deleted";
            if (TextNav.NextGrapheme(Rope, Cursor) is not int next)
                return null;
            if (TextNav.GraphemeAt(Rope, Cursor) is not string deleted)
                return null;
            Splice(Cursor, next, "");
            PreferredColumn = null;
            return SpeechRenderer.SpeakChar(deleted);
        }
        finally
        {
            EndOp();
        }
    }

    /// <summary>Delete the word before the cursor (Notepad-style: word +
    /// trailing delimiters). Returns the deleted text.</summary>
    public string? DeleteWordBackward()
    {
        if (ReadOnly)
            return null;
        BeginOp(bulk: true);
        try
        {
            if (DeleteSelectionSilent())
                return "deleted";
            if (Cursor == 0)
                return null;
            var target = TextNav.PrevWordExtent(Rope, Cursor);
            var deleted = Splice(target, Cursor, "");
            Cursor = target;
            PreferredColumn = null;
            return deleted;
        }
        finally
        {
            EndOp();
        }
    }

    /// <summary>Delete the word after the cursor (Notepad-style).</summary>
    public string? DeleteWordForward()
    {
        if (ReadOnly)
            return null;
        BeginOp(bulk: true);
        try
        {
            if (DeleteSelectionSilent())
                return "deleted";
            if (Cursor >= Length)
                return null;
            var target = TextNav.NextWordExtent(Rope, Cursor);
            var deleted = Splice(Cursor, target, "");
            PreferredColumn = null;
            return deleted;
        }
        finally
        {
            EndOp();
        }
    }

    // ── Movement operations ──

    private string GraphemeSpeechAt(int pos) =>
        TextNav.GraphemeAt(Rope, pos) is string g ? SpeechRenderer.SpeakChar(g) : "blank";

    /// <summary>Move left one grapheme. Returns the character at the new
    /// position or "blank".</summary>
    public string MoveLeft()
    {
        if (CollapseSelectionDirectional(false))
            return GraphemeSpeechAt(Cursor);
        if (TextNav.PrevGrapheme(Rope, Cursor) is int pos)
        {
            Cursor = pos;
            PreferredColumn = null;
            return GraphemeSpeechAt(pos);
        }
        return "blank";
    }

    /// <summary>Move right one grapheme.</summary>
    public string MoveRight()
    {
        if (CollapseSelectionDirectional(true))
            return GraphemeSpeechAt(Cursor);
        if (TextNav.NextGrapheme(Rope, Cursor) is int pos)
        {
            Cursor = pos;
            PreferredColumn = null;
            return GraphemeSpeechAt(pos);
        }
        return "blank";
    }

    /// <summary>Move left one word (Windows Ctrl+Left): land on the start
    /// of the current word, or of the previous one. Speaks the word.</summary>
    public string MoveWordLeft()
    {
        if (CollapseSelectionDirectional(false))
            return TextNav.WordAt(Rope, Cursor);
        var target = TextNav.PrevWordStart(Rope, Cursor);
        if (target == Cursor)
            return TextNav.WordAt(Rope, Cursor);
        Cursor = target;
        PreferredColumn = null;
        return TextNav.WordAt(Rope, Cursor);
    }

    /// <summary>Move right one word (Windows Ctrl+Right): land on the
    /// start of the next word. Speaks the word.</summary>
    public string MoveWordRight()
    {
        if (CollapseSelectionDirectional(true))
            return TextNav.WordAt(Rope, Cursor);
        var target = TextNav.NextWordStart(Rope, Cursor);
        if (target == Cursor)
            return TextNav.WordAt(Rope, Cursor);
        Cursor = target;
        PreferredColumn = null;
        return TextNav.WordAt(Rope, Cursor);
    }

    /// <summary>Move to the line start (Home).</summary>
    public string MoveToLineStart()
    {
        Selection = null;
        var start = TextNav.LineStart(Rope, Cursor);
        Cursor = start;
        PreferredColumn = null;
        return GraphemeSpeechAt(start);
    }

    /// <summary>Move to the line end (End).</summary>
    public string MoveToLineEnd()
    {
        Selection = null;
        var end = TextNav.LineEnd(Rope, Cursor);
        Cursor = end;
        PreferredColumn = null;
        var lineStart = TextNav.LineStart(Rope, end);
        if (end > lineStart)
        {
            return TextNav.GraphemeBefore(Rope, end) is string g && g != "\n"
                ? SpeechRenderer.SpeakChar(g)
                : "blank";
        }
        return "blank";
    }

    /// <summary>Move to the document start (Ctrl+Home).</summary>
    public string MoveToDocStart()
    {
        Selection = null;
        Cursor = 0;
        PreferredColumn = null;
        return GraphemeSpeechAt(0);
    }

    /// <summary>Move to the document end (Ctrl+End).</summary>
    public string MoveToDocEnd()
    {
        Selection = null;
        Cursor = Length;
        PreferredColumn = null;
        if (Cursor == 0)
            return "blank";
        if (TextNav.GraphemeBefore(Rope, Cursor) is not string g)
            return "blank";
        if (g == "\n" || g == "\r")
        {
            var prev = TextNav.PrevGrapheme(Rope, Math.Max(Cursor - 1, 0));
            return prev is int p && TextNav.GraphemeAt(Rope, p) is string g2
                ? SpeechRenderer.SpeakChar(g2)
                : "blank";
        }
        return SpeechRenderer.SpeakChar(g);
    }

    private int CurrentColumn() => Cursor - TextNav.LineStart(Rope, Cursor);

    /// <summary>Move up one line (multiline only). Speaks the landed line.</summary>
    public string MoveLineUp()
    {
        if (!Multiline)
            return ReadAll();
        Selection = null;
        var column = PreferredColumn ?? CurrentColumn();
        if (TextNav.LineUp(Rope, Cursor, column) is (var pos, var newColumn))
        {
            Cursor = pos;
            PreferredColumn = newColumn;
            var line = TextNav.CurrentLineText(Rope, pos);
            return line.Length == 0 ? "blank" : line;
        }
        return "top";
    }

    /// <summary>Move down one line (multiline only).</summary>
    public string MoveLineDown()
    {
        if (!Multiline)
            return ReadAll();
        Selection = null;
        var column = PreferredColumn ?? CurrentColumn();
        if (TextNav.LineDown(Rope, Cursor, column) is (var pos, var newColumn))
        {
            Cursor = pos;
            PreferredColumn = newColumn;
            var line = TextNav.CurrentLineText(Rope, pos);
            return line.Length == 0 ? "blank" : line;
        }
        return "bottom";
    }

    // ── Selection operations ──

    private int SelectionAnchor() => Selection is (var anchor, _) ? anchor : Cursor;

    public string SelectLeft()
    {
        var anchor = SelectionAnchor();
        if (TextNav.PrevGrapheme(Rope, Cursor) is int pos)
        {
            Cursor = pos;
            Selection = (anchor, Cursor);
            PreferredColumn = null;
            return DescribeSelection();
        }
        return "blank";
    }

    public string SelectRight()
    {
        var anchor = SelectionAnchor();
        if (TextNav.NextGrapheme(Rope, Cursor) is int pos)
        {
            Cursor = pos;
            Selection = (anchor, Cursor);
            PreferredColumn = null;
            return DescribeSelection();
        }
        return "blank";
    }

    public string SelectWordLeft()
    {
        var anchor = SelectionAnchor();
        var target = TextNav.PrevWordExtent(Rope, Cursor);
        if (target == Cursor)
            return "blank";
        Cursor = target;
        Selection = (anchor, Cursor);
        PreferredColumn = null;
        return DescribeSelection();
    }

    public string SelectWordRight()
    {
        var anchor = SelectionAnchor();
        var target = TextNav.NextWordExtent(Rope, Cursor);
        if (target == Cursor)
            return "blank";
        Cursor = target;
        Selection = (anchor, Cursor);
        PreferredColumn = null;
        return DescribeSelection();
    }

    public string SelectToLineStart()
    {
        var anchor = SelectionAnchor();
        Cursor = TextNav.LineStart(Rope, Cursor);
        Selection = (anchor, Cursor);
        PreferredColumn = null;
        return DescribeSelection();
    }

    public string SelectToLineEnd()
    {
        var anchor = SelectionAnchor();
        Cursor = TextNav.LineEnd(Rope, Cursor);
        Selection = (anchor, Cursor);
        PreferredColumn = null;
        return DescribeSelection();
    }

    public string SelectToDocStart()
    {
        var anchor = SelectionAnchor();
        Cursor = 0;
        Selection = (anchor, Cursor);
        PreferredColumn = null;
        return DescribeSelection();
    }

    public string SelectToDocEnd()
    {
        var anchor = SelectionAnchor();
        Cursor = Length;
        Selection = (anchor, Cursor);
        PreferredColumn = null;
        return DescribeSelection();
    }

    public string SelectLineUp()
    {
        if (!Multiline)
            return SelectToLineStart();
        var anchor = SelectionAnchor();
        var column = PreferredColumn ?? CurrentColumn();
        if (TextNav.LineUp(Rope, Cursor, column) is (var pos, var newColumn))
        {
            Cursor = pos;
            Selection = (anchor, Cursor);
            PreferredColumn = newColumn;
            return DescribeSelection();
        }
        return "top";
    }

    public string SelectLineDown()
    {
        if (!Multiline)
            return SelectToLineEnd();
        var anchor = SelectionAnchor();
        var column = PreferredColumn ?? CurrentColumn();
        if (TextNav.LineDown(Rope, Cursor, column) is (var pos, var newColumn))
        {
            Cursor = pos;
            Selection = (anchor, Cursor);
            PreferredColumn = newColumn;
            return DescribeSelection();
        }
        return "bottom";
    }

    public string SelectAll()
    {
        var length = Length;
        if (length == 0)
            return "blank";
        Selection = (0, length);
        Cursor = length;
        return length > SpeechRenderer.SpeakLimit
            ? $"{length} characters selected"
            : $"{Text()} selected";
    }

    // ── Clipboard operations ──

    /// <summary>Copy selected text. Returns (clipboard content, speech).</summary>
    public (string Clip, string Speech) Copy() =>
        SelectedText() is string text ? (text, "copied") : ("", "");

    /// <summary>Cut selected text. Returns (clipboard content, speech).</summary>
    public (string Clip, string Speech) Cut()
    {
        if (ReadOnly)
            return ("", "");
        if (SelectedText() is not string text)
            return ("", "");
        DeleteSelectionSilent();
        return (text, "cut");
    }

    /// <summary>Paste text at the cursor. For single-line editors,
    /// newlines become spaces and CRs are removed.</summary>
    public string Paste(string text)
    {
        if (ReadOnly)
            return "";
        BeginOp(bulk: true);
        try
        {
            var hadSelection = DeleteSelectionSilent();
            if (!Multiline)
                text = text.Replace('\n', ' ').Replace("\r", "");
            Splice(Cursor, Cursor, text);
            Cursor += text.Length;
            Selection = null;
            PreferredColumn = null;
            return hadSelection ? "selection removed, pasted" : "pasted";
        }
        finally
        {
            EndOp();
        }
    }

    /// <summary>Programmatic insert at the cursor, replacing an active
    /// selection: one undo unit. Ignores ReadOnly, like the widget
    /// surface it backs. Returns whether a selection was replaced.</summary>
    public bool ProgrammaticInsert(string text)
    {
        BeginOp(bulk: true);
        try
        {
            var hadSelection = DeleteSelectionSilent();
            if (text.Length != 0)
            {
                Splice(Cursor, Cursor, text);
                Cursor += text.Length;
            }
            PreferredColumn = null;
            return hadSelection;
        }
        finally
        {
            EndOp();
        }
    }

    /// <summary>Programmatic range replacement (from ≤ to, both on
    /// grapheme boundaries): one undo unit, cursor and selection mapped
    /// through the edit. Ignores ReadOnly.</summary>
    public void ProgrammaticReplace(int from, int to, string text)
    {
        if (from == to && text.Length == 0)
            return;
        BeginOp(bulk: true);
        try
        {
            Splice(from, to, text);

            int Map(int position) => position <= from ? position
                : position >= to ? position + text.Length - (to - from)
                : from + text.Length;
            if (Selection is (var anchor, var cursor))
            {
                var mappedAnchor = Map(anchor);
                var mappedCursor = Map(cursor);
                Selection = mappedAnchor == mappedCursor ? null : (mappedAnchor, mappedCursor);
                Cursor = mappedCursor;
            }
            else
            {
                Cursor = Map(Cursor);
            }
            PreferredColumn = null;
        }
        finally
        {
            EndOp();
        }
    }

    // ── Internal helpers ──

    /// <summary>The selected text, if any.</summary>
    public string? SelectedText()
    {
        if (Selection is not (var anchor, var cursor))
            return null;
        var start = Math.Min(anchor, cursor);
        var end = Math.Max(anchor, cursor);
        return start == end ? null : Rope.Substring(start, end);
    }

    /// <summary>The number of selected code units, without materializing
    /// the text.</summary>
    public int SelectionCharCount() =>
        Selection is (var a, var c) ? Math.Abs(a - c) : 0;

    /// <summary>Delete the current selection (if any). True if something
    /// was deleted.</summary>
    public bool DeleteSelectionSilent()
    {
        if (Selection is not (var anchor, var cursor))
            return false;
        var start = Math.Min(anchor, cursor);
        var end = Math.Max(anchor, cursor);
        if (start >= end)
        {
            Selection = null;
            return false;
        }
        BeginOp(bulk: true);
        try
        {
            Selection = null;
            Splice(start, end, "");
            Cursor = start;
            return true;
        }
        finally
        {
            EndOp();
        }
    }

    private string DescribeSelection()
    {
        var count = SelectionCharCount();
        if (count == 0)
            return "blank";
        if (count > SpeechRenderer.SpeakLimit)
            return $"{count} characters selected";
        return SelectedText() is string text ? $"{text} selected" : "blank";
    }

    /// <summary>Replace the content (cursor clamped onto a grapheme
    /// boundary, selection cleared). No-op when the text already matches
    /// — chunk compare, no rope materialization. Clears the undo
    /// history: this is a different document, not an edit.</summary>
    public void SetText(string text)
    {
        if (!Rope.ContentEquals(text))
        {
            Rope = new Rope(text);
            Cursor = TextNav.SnapToGraphemeBoundary(Rope, Cursor);
            Selection = null;
            History.Clear();
        }
    }

    /// <summary>Extract a range as a string (clamped).</summary>
    public string SliceToString(int start, int end) => Rope.Substring(start, end);
}
