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

    public EditorState(string text, bool multiline)
    {
        Rope = new Rope(text);
        Multiline = multiline;
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
    public string InsertChar(char ch)
    {
        if (ReadOnly)
            return "";
        var hadSelection = DeleteSelectionSilent();
        var s = ch.ToString();
        Rope.Insert(Cursor, s);
        Cursor += 1;
        Selection = null;
        PreferredColumn = null;
        var charSpeech = SpeechRenderer.SpeakChar(s);
        return hadSelection ? $"selection removed, {charSpeech}" : charSpeech;
    }

    /// <summary>Insert a newline at the cursor (multiline only).</summary>
    public string InsertNewline()
    {
        if (!Multiline || ReadOnly)
            return "";
        var hadSelection = DeleteSelectionSilent();
        Rope.Insert(Cursor, "\n");
        Cursor += 1;
        Selection = null;
        PreferredColumn = null;
        var speech = SpeechRenderer.SpeakChar("\n");
        return hadSelection ? $"selection removed, {speech}" : speech;
    }

    /// <summary>Delete the grapheme before the cursor. Returns speech for
    /// the deleted character, or null when there was nothing to delete.</summary>
    public string? DeleteBackward()
    {
        if (ReadOnly)
            return null;
        if (DeleteSelectionSilent())
            return "deleted";
        if (Cursor == 0)
            return null;
        if (TextNav.PrevGrapheme(Rope, Cursor) is not int prev)
            return null;
        if (TextNav.GraphemeAt(Rope, prev) is not string deleted)
            return null;
        Rope.Remove(prev, Cursor);
        Cursor = prev;
        PreferredColumn = null;
        return SpeechRenderer.SpeakChar(deleted);
    }

    /// <summary>Delete the grapheme after the cursor.</summary>
    public string? DeleteForward()
    {
        if (ReadOnly)
            return null;
        if (DeleteSelectionSilent())
            return "deleted";
        if (TextNav.NextGrapheme(Rope, Cursor) is not int next)
            return null;
        if (TextNav.GraphemeAt(Rope, Cursor) is not string deleted)
            return null;
        Rope.Remove(Cursor, next);
        PreferredColumn = null;
        return SpeechRenderer.SpeakChar(deleted);
    }

    /// <summary>Delete the word before the cursor (Notepad-style: word +
    /// trailing delimiters). Returns the deleted text.</summary>
    public string? DeleteWordBackward()
    {
        if (ReadOnly)
            return null;
        if (DeleteSelectionSilent())
            return "deleted";
        if (Cursor == 0)
            return null;
        var target = TextNav.PrevWordExtent(Rope, Cursor);
        var deleted = Rope.Substring(target, Cursor);
        Rope.Remove(target, Cursor);
        Cursor = target;
        PreferredColumn = null;
        return deleted;
    }

    /// <summary>Delete the word after the cursor (Notepad-style).</summary>
    public string? DeleteWordForward()
    {
        if (ReadOnly)
            return null;
        if (DeleteSelectionSilent())
            return "deleted";
        if (Cursor >= Length)
            return null;
        var target = TextNav.NextWordExtent(Rope, Cursor);
        var deleted = Rope.Substring(Cursor, target);
        Rope.Remove(Cursor, target);
        PreferredColumn = null;
        return deleted;
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
        var hadSelection = DeleteSelectionSilent();
        if (!Multiline)
            text = text.Replace('\n', ' ').Replace("\r", "");
        Rope.Insert(Cursor, text);
        Cursor += text.Length;
        Selection = null;
        PreferredColumn = null;
        return hadSelection ? "selection removed, pasted" : "pasted";
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
        if (Selection is (var anchor, var cursor))
        {
            Selection = null;
            var start = Math.Min(anchor, cursor);
            var end = Math.Max(anchor, cursor);
            if (start < end)
            {
                Rope.Remove(start, end);
                Cursor = start;
                return true;
            }
        }
        return false;
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

    /// <summary>Replace the content (cursor clamped, selection cleared).
    /// No-op when the text already matches — chunk compare, no rope
    /// materialization.</summary>
    public void SetText(string text)
    {
        if (!Rope.ContentEquals(text))
        {
            Rope = new Rope(text);
            Cursor = Math.Min(Cursor, Rope.Length);
            Selection = null;
        }
    }

    /// <summary>Extract a range as a string (clamped).</summary>
    public string SliceToString(int start, int end) => Rope.Substring(start, end);
}
