using Srui.Core;

namespace Srui;

/// <summary>Single- or multi-line text editor with full cursor
/// navigation, selection, clipboard, and typing echo. Enter inserts a
/// newline in multiline editors and falls through to the layer's primary
/// widget in single-line (and read-only) ones. Positions on this surface
/// (<see cref="CursorPosition"/>, <see cref="Selection"/>) are UTF-16
/// code-unit offsets into <see cref="Text"/>; setters clamp to the text
/// and snap out of the middle of a surrogate pair.</summary>
public class EditBox : Widget
{
    private readonly EditorState _editor;

    public EditBox(IWidgetContainer parent, string? name, string text = "", bool multiline = false)
        : base(parent, name, RoleTextFor(false, multiline))
    {
        _editor = new EditorState(text, multiline);
    }

    /// <summary>The current line (multiline) or the text (single-line),
    /// pulled fresh at announcement time.</summary>
    protected internal override string ValueText => EditBoxCore.LabelValue(_editor);

    private static string RoleTextFor(bool readOnly, bool multiline) => (readOnly, multiline) switch
    {
        (false, false) => "edit",
        (true, false) => "edit read only",
        (false, true) => "edit multi line",
        (true, true) => "edit read only multi line",
    };

    public bool Multiline => _editor.Multiline;

    /// <summary>The full text. Setting replaces the content (cursor
    /// clamped, selection cleared) and speaks the new value when focused.</summary>
    public string Text
    {
        get => _editor.Text();
        set => Engine.UpdateLabel(Node, _ => _editor.SetText(value));
    }

    /// <summary>A read-only editor swallows typing silently and lets
    /// Enter fall through to the layer's primary. Toggling speaks the
    /// new role text when focused.</summary>
    public bool ReadOnly
    {
        get => _editor.ReadOnly;
        set => Engine.UpdateLabel(Node, label =>
        {
            _editor.ReadOnly = value;
            label.RoleText = RoleTextFor(value, _editor.Multiline);
        });
    }

    /// <summary>Text length in UTF-16 code units.</summary>
    public int Length => _editor.Length;

    /// <summary>The cursor position. Setting clears the selection; a move
    /// while focused speaks the character at the new position, exactly as
    /// a user-driven cursor move would.</summary>
    public int CursorPosition
    {
        get => _editor.Cursor;
        set
        {
            var target = Snap(value);
            var moved = target != _editor.Cursor || _editor.HasSelection;
            _editor.Selection = null;
            _editor.PreferredColumn = null;
            _editor.Cursor = target;
            if (moved && IsFocused && !_editor.IsEmpty)
                Promulgate(EditBoxCore.CharNavEvent(this, _editor));
        }
    }

    /// <summary>The selection as (anchor, cursor), or null when nothing
    /// is selected. Setting while focused speaks the selected text ("…
    /// selected"), or "Selection removed" when clearing, like the
    /// user-driven equivalents.</summary>
    public (int Anchor, int Cursor)? Selection
    {
        get => _editor.HasSelection ? _editor.Selection : null;
        set
        {
            if (value is (var anchor, var cursor))
            {
                var a = Snap(anchor);
                var c = Snap(cursor);
                _editor.Selection = (a, c);
                _editor.Cursor = c;
                _editor.PreferredColumn = null;
                if (a != c && IsFocused)
                {
                    var start = Math.Min(a, c);
                    var end = Math.Max(a, c);
                    var delta = end - start > SpeechRenderer.SpeakLimit
                        ? $"{end - start} characters"
                        : _editor.SliceToString(start, end);
                    Promulgate(new AccessibilityEvent.Selection(this, delta, SelectionKind.Selected));
                }
            }
            else
            {
                var had = _editor.HasSelection;
                _editor.Selection = null;
                if (had && IsFocused)
                    Promulgate(new AccessibilityEvent.Selection(this, "", SelectionKind.Cleared));
            }
        }
    }

    /// <summary>The selected text, or "" when nothing is selected.</summary>
    public string SelectedText => _editor.SelectedText() ?? "";

    /// <summary>Select everything, announcing like Ctrl+A.</summary>
    public void SelectAll()
    {
        _editor.SelectAll();
        if (_editor.IsEmpty || !IsFocused)
            return;
        var length = _editor.Length;
        var delta = length > SpeechRenderer.SpeakLimit
            ? $"{length} characters"
            : _editor.Text();
        Promulgate(new AccessibilityEvent.Selection(this, delta, SelectionKind.All));
    }

    // ── Programmatic editing ──
    // Like the Text setter, these ignore ReadOnly, which guards user
    // input, not the program.

    /// <summary>Insert text at the cursor, replacing an active selection
    /// — the programmatic form of typing it, and announced the same way
    /// when focused: the inserted text (after "Selection removed" when
    /// one was replaced). The cursor lands after the insertion.
    /// Single-line editors flatten newlines to spaces, like paste.</summary>
    public void InsertText(string text)
    {
        if (!Multiline)
            text = text.Replace('\n', ' ').Replace("\r", "");
        var hadSelection = _editor.DeleteSelectionSilent();
        if (!hadSelection && text.Length == 0)
            return;
        _editor.Rope.Insert(_editor.Cursor, text);
        _editor.Cursor += text.Length;
        _editor.PreferredColumn = null;
        if (!IsFocused)
            return;
        if (hadSelection)
            Promulgate(new AccessibilityEvent.Selection(this, "", SelectionKind.Cleared));
        if (text.Length != 0)
            Promulgate(new AccessibilityEvent.Typing(this, text, null, TypingKind.Insert));
    }

    /// <summary>Replace the range between two positions (either order;
    /// clamped and snapped) with new text — the silent structural splice
    /// under find-and-replace and its kin. The caller owns any
    /// announcement, like a list's SetItem. The cursor and the selection
    /// endpoints follow the splice: positions before it keep their place,
    /// positions after it shift with the length change, and positions
    /// inside it land at the end of the new text. Single-line editors
    /// flatten newlines to spaces.</summary>
    public void ReplaceRange(int start, int end, string text)
    {
        var from = Snap(start);
        var to = Snap(end);
        if (from > to)
            (from, to) = (to, from);
        if (!Multiline)
            text = text.Replace('\n', ' ').Replace("\r", "");
        if (from == to && text.Length == 0)
            return;
        _editor.Rope.Remove(from, to);
        _editor.Rope.Insert(from, text);

        int Map(int position) => position <= from ? position
            : position >= to ? position + text.Length - (to - from)
            : from + text.Length;
        if (_editor.Selection is (var anchor, var cursor))
        {
            var mappedAnchor = Map(anchor);
            var mappedCursor = Map(cursor);
            _editor.Selection = mappedAnchor == mappedCursor ? null : (mappedAnchor, mappedCursor);
            _editor.Cursor = mappedCursor;
        }
        else
        {
            _editor.Cursor = Map(_editor.Cursor);
        }
        _editor.PreferredColumn = null;
    }

    // ── Announced movement ──
    // Each method runs the same handling path as the key it names, so a
    // programmatic move speaks exactly what the user-driven one would —
    // when the widget is focused; unfocused, it moves silently.

    /// <summary>Run a user-equivalent navigation input against the
    /// editor, speaking its feedback when focused.</summary>
    private void Nav(InputKind kind)
    {
        var result = EditBoxCore.Handle(this, InputEvent.Simple(kind), _editor, Engine.Clipboard);
        if (!IsFocused)
            return;
        foreach (var ev in result.Events)
            Promulgate(ev);
    }

    /// <summary>Move left one character, announcing like Left arrow (an
    /// active selection collapses to its start).</summary>
    public void MoveLeft() => Nav(InputKind.MoveLeft);

    /// <summary>Move right one character, announcing like Right arrow (an
    /// active selection collapses to its end).</summary>
    public void MoveRight() => Nav(InputKind.MoveRight);

    /// <summary>Move to the start of the current or previous word,
    /// announcing like Ctrl+Left.</summary>
    public void MoveWordLeft() => Nav(InputKind.MoveWordLeft);

    /// <summary>Move to the start of the next word, announcing like
    /// Ctrl+Right.</summary>
    public void MoveWordRight() => Nav(InputKind.MoveWordRight);

    /// <summary>Move to the line start, announcing like Home.</summary>
    public void MoveToLineStart() => Nav(InputKind.MoveToLineStart);

    /// <summary>Move to the line end, announcing like End.</summary>
    public void MoveToLineEnd() => Nav(InputKind.MoveToLineEnd);

    /// <summary>Move up one line, announcing the landed line like Up
    /// arrow (multiline; a single-line editor reports the boundary).</summary>
    public void MoveLineUp() => Nav(InputKind.MoveLineUp);

    /// <summary>Move down one line, announcing the landed line like Down
    /// arrow.</summary>
    public void MoveLineDown() => Nav(InputKind.MoveLineDown);

    /// <summary>Move to the text start, announcing like Ctrl+Home.</summary>
    public void MoveToDocStart() => Nav(InputKind.MoveToDocStart);

    /// <summary>Move to the text end, announcing like Ctrl+End.</summary>
    public void MoveToDocEnd() => Nav(InputKind.MoveToDocEnd);

    /// <summary>Replace the text without any announcement — the
    /// counterpart of the <see cref="Text"/> setter for subclass input
    /// handlers, which mutate state silently and then emit what the user
    /// should hear. The cursor lands at the end of the new text and the
    /// selection is cleared — positioned to continue typing.</summary>
    protected void SetTextSilently(string text)
    {
        _editor.SetText(text);
        _editor.Selection = null;
        _editor.Cursor = _editor.Length;
        _editor.PreferredColumn = null;
    }

    // ── Position queries ──
    // Pure reads over the text engine: no state changes, no announcements.
    // Positions clamp to the text and snap off surrogate halves, like the
    // position setters. Lines and columns are 0-based; a column is the
    // UTF-16 code-unit offset from its line start.

    /// <summary>The start of the word at or before the position — where
    /// Ctrl+Left from there would land.</summary>
    public int PreviousWordStart(int position) => TextNav.PrevWordStart(_editor.Rope, Snap(position));

    /// <summary>The start of the next word — where Ctrl+Right would land
    /// (the text end when no next word exists).</summary>
    public int NextWordStart(int position) => TextNav.NextWordStart(_editor.Rope, Snap(position));

    /// <summary>The start of the word extent (word plus trailing
    /// separators) before the position — the range Ctrl+Backspace deletes
    /// and Ctrl+Shift+Left selects.</summary>
    public int PreviousWordExtent(int position) => TextNav.PrevWordExtent(_editor.Rope, Snap(position));

    /// <summary>The end of the word extent after the position —
    /// Ctrl+Delete and Ctrl+Shift+Right.</summary>
    public int NextWordExtent(int position) => TextNav.NextWordExtent(_editor.Rope, Snap(position));

    /// <summary>The word surrounding or starting at the position; a
    /// non-word character yields just that character, the text end "".</summary>
    public string WordAt(int position) => TextNav.WordAt(_editor.Rope, Snap(position));

    /// <summary>The start of the line containing the position.</summary>
    public int LineStartAt(int position) => TextNav.LineStart(_editor.Rope, Snap(position));

    /// <summary>The end of the line containing the position (before its
    /// newline / CRLF, or the text end).</summary>
    public int LineEndAt(int position) => TextNav.LineEnd(_editor.Rope, Snap(position));

    /// <summary>The text of the line containing the position, without its
    /// terminator.</summary>
    public string LineTextAt(int position) => TextNav.CurrentLineText(_editor.Rope, Snap(position));

    /// <summary>Number of lines in the text (newlines + 1).</summary>
    public int LineCount => TextNav.LineCount(_editor.Rope);

    /// <summary>The 0-based line and column of a position.</summary>
    public (int Line, int Column) LineColumnAt(int position) =>
        TextNav.LineColumnAt(_editor.Rope, Snap(position));

    /// <summary>The position of a 0-based (line, column): the line clamps
    /// to the text's last line, the column to the addressed line's end,
    /// and the result snaps off surrogate halves.</summary>
    public int PositionAt(int line, int column) =>
        Snap(TextNav.PositionOfLineColumn(_editor.Rope, Math.Max(line, 0), Math.Max(column, 0)));

    /// <summary>Clamp a position to the text and snap it off the middle
    /// of a surrogate pair.</summary>
    private int Snap(int position)
    {
        var pos = Math.Clamp(position, 0, _editor.Length);
        if (pos > 0 && pos < _editor.Length && char.IsLowSurrogate(_editor.Rope.CharAt(pos)))
            pos--;
        return pos;
    }

    public override bool ReservesKey(KeyCombo combo)
    {
        // Unmodified typing, navigation, and deletion.
        if (!combo.Ctrl && !combo.Alt && !combo.Shift)
        {
            if (combo.Key.IsChar(out _)) return true;
            if (combo.Key == Key.Space || combo.Key == Key.Enter) return true;
            if (combo.Key == Key.Up || combo.Key == Key.Down
                || combo.Key == Key.Left || combo.Key == Key.Right) return true;
            if (combo.Key == Key.Home || combo.Key == Key.End) return true;
            if (combo.Key == Key.Backspace || combo.Key == Key.Delete) return true;
        }
        // Shift+movement (selection), Shift+Backspace (backspace),
        // Shift+Delete (cut).
        if (combo.Shift && !combo.Ctrl && !combo.Alt)
        {
            if (combo.Key == Key.Left || combo.Key == Key.Right
                || combo.Key == Key.Up || combo.Key == Key.Down
                || combo.Key == Key.Home || combo.Key == Key.End
                || combo.Key == Key.Backspace || combo.Key == Key.Delete) return true;
        }
        // Ctrl+movement/editing.
        if (combo.Ctrl && !combo.Alt)
        {
            if (combo.Key == Key.Left || combo.Key == Key.Right
                || combo.Key == Key.Home || combo.Key == Key.End) return true;
            if (combo.Key == Key.Backspace || combo.Key == Key.Delete) return true;
        }
        // Ctrl+clipboard/select-all.
        if (combo.Ctrl && !combo.Alt && !combo.Shift && combo.Key.IsChar(out var c)
            && c is 'c' or 'x' or 'v' or 'a') return true;
        return false;
    }

    protected override bool OnInput(in InputEvent input)
    {
        var result = EditBoxCore.Handle(this, input, _editor, Engine.Clipboard);
        if (!result.Consumed)
            return false;
        foreach (var ev in result.Events)
            Promulgate(ev);
        if (result.Changed)
            PostChanged();
        return true;
    }
}
