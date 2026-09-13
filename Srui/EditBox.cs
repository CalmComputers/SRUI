using Srui.Core;

namespace Srui;

/// <summary>Single- or multi-line text editor with full cursor
/// navigation, selection, clipboard, typing echo, and multi-level undo
/// (Ctrl+Z, redo on Ctrl+Y or Ctrl+Shift+Z; see <see cref="Undo"/>).
/// Enter inserts a
/// newline in multiline editors and falls through to the layer's primary
/// widget in single-line (and read-only) ones. Positions on this surface
/// (<see cref="CursorPosition"/>, <see cref="Selection"/>) are UTF-16
/// code-unit offsets into <see cref="Text"/>; setters clamp to the text
/// and snap backward onto a grapheme cluster boundary, so a position can
/// never land inside a surrogate pair, a combining sequence, or a CRLF.
///
/// The edit box is the one widget whose own action events are the
/// better voice for its changes: typing speaks the character, a move
/// speaks what it landed on. So its editing paths suppress the line
/// and selection fields for the tick, and only a programmatic
/// <see cref="Text"/> replacement or cursor placement reads through the
/// fields.</summary>
public partial class EditBox : Widget
{
    private readonly EditorState _editor;

    public EditBox(IWidgetContainer parent, string? name, string text = "", bool multiline = false)
        : base(parent, name, Role.Edit)
    {
        _editor = new EditorState(text, multiline);
        SelectAllOnFocus = !multiline;
    }

    // ── Fields ──

    /// <summary>The current line (multiline) or the text (single-line).</summary>
    [Field] public string? Value => _editor.CurrentLine();

    /// <summary>The selected text in spoken form (masked in a password
    /// field, a count past the speak limit), or null when nothing is
    /// selected; readers speak it in place of the line.</summary>
    [Field] public string? SelectedText => EditBoxCore.SelectedText(_editor);

    /// <summary>A read-only editor swallows typing silently and lets
    /// Enter fall through to the layer's primary.</summary>
    [Field]
    public bool ReadOnly
    {
        get => _editor.ReadOnly;
        set
        {
            _editor.ReadOnly = value;
            Engine.Touch();
        }
    }

    [Field] public bool Multiline => _editor.Multiline;

    /// <summary>A password field. The text is kept and read back as
    /// usual; what reaches the user is masked - the value, typing echo,
    /// cursor and word movement, selection, undo, and deletion all
    /// speak one star per character, a completed word is never echoed,
    /// and copy and cut are refused (paste still lands). Focus hears
    /// "protected" after the value.</summary>
    [Field]
    public bool Password
    {
        get => _editor.Masked;
        set
        {
            _editor.Masked = value;
            Engine.Touch();
        }
    }

    /// <summary>The full text. Setting replaces the content (cursor
    /// clamped onto a grapheme boundary, selection cleared, undo
    /// history dropped — this is a new document, not an edit); the
    /// tick end reads the new line.</summary>
    public string Text
    {
        get => _editor.Text();
        set
        {
            _editor.SetText(value);
            // A new document: the selection that went with the old one
            // is not news, the new line is.
            Suppress(Fields.SelectedText);
        }
    }

    /// <summary>Text length in UTF-16 code units.</summary>
    public int Length => _editor.Length;

    /// <summary>The cursor position. Setting clears the selection; the
    /// tick end reads the line if it changed.</summary>
    public int CursorPosition
    {
        get => _editor.Cursor;
        set
        {
            _editor.Selection = null;
            _editor.PreferredColumn = null;
            _editor.Cursor = Snap(value);
            Engine.Touch();
        }
    }

    /// <summary>The selection as (anchor, cursor), or null when nothing
    /// is selected. The tick end reads the selected text, or that the
    /// selection went.</summary>
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
            }
            else
            {
                _editor.Selection = null;
            }
            _editor.PreferredColumn = null;
            Engine.Touch();
        }
    }

    /// <summary>Select the whole text every time focus enters, so
    /// typing into a seeded field replaces the default instead of
    /// concatenating (tabbing to a "0" and typing "6" gives 6, not
    /// 60) — the Windows edit-control convention. On by default for
    /// single-line editors, off for multiline ones, where a focus
    /// visit must not clobber a working selection; assign to override
    /// either way. Off touches nothing: the box keeps whatever
    /// selection it already had, possibly none.</summary>
    public bool SelectAllOnFocus { get; set; }

    protected internal override void OnFocusGained()
    {
        if (SelectAllOnFocus && !_editor.IsEmpty)
            _editor.SelectAll();
    }

    /// <summary>Select everything, announcing like Ctrl+A.</summary>
    public void SelectAll() => Nav(InputKind.SelectAll);

    // ── Programmatic editing ──
    // Like the Text setter, these ignore ReadOnly, which guards user
    // input, not the program.

    /// <summary>Insert text at the cursor, replacing an active selection
    /// — the programmatic form of typing it, and announced the same way:
    /// the inserted text (after "Selection removed" when one was
    /// replaced). The cursor lands after the insertion. Single-line
    /// editors flatten newlines to spaces, like paste.</summary>
    public void InsertText(string text)
    {
        if (!Multiline)
            text = text.Replace('\n', ' ').Replace("\r", "");
        if (!_editor.HasSelection && text.Length == 0)
            return;
        var hadSelection = _editor.ProgrammaticInsert(text);
        Quiet();
        if (hadSelection)
            Promulgate(new AccessibilityEvent.Selection(this, "", SelectionKind.Cleared));
        if (text.Length != 0)
            Promulgate(new AccessibilityEvent.Typing(this, _editor.Spoken(text), null, TypingKind.Insert));
    }

    /// <summary>Replace the range between two positions (either order;
    /// clamped and snapped) with new text — the silent structural splice
    /// under find-and-replace and its kin. The caller owns any
    /// announcement. The cursor and the selection endpoints follow the
    /// splice: positions before it keep their place, positions after it
    /// shift with the length change, and positions inside it land at
    /// the end of the new text. Single-line editors flatten newlines to
    /// spaces.</summary>
    public void ReplaceRange(int start, int end, string text)
    {
        var from = Snap(start);
        var to = Snap(end);
        if (from > to)
            (from, to) = (to, from);
        if (!Multiline)
            text = text.Replace('\n', ' ').Replace("\r", "");
        _editor.ProgrammaticReplace(from, to, text);
        Quiet();
    }

    /// <summary>The editor's action events are the voice for this
    /// tick: keep the line and selection fields out of the reading.</summary>
    private void Quiet() => Suppress(Fields.Value, Fields.SelectedText);

    // ── Announced movement ──
    // Each method runs the same handling path as the key it names, so a
    // programmatic move speaks exactly what the user-driven one would.

    /// <summary>Run a user-equivalent input against the editor,
    /// speaking its feedback and raising Changed when it edited.</summary>
    private void Nav(InputKind kind)
    {
        var result = EditBoxCore.Handle(
            this, InputEvent.Simple(kind), _editor, Engine.Clipboard, NowMs);
        Quiet();
        foreach (var ev in result.Events)
            Promulgate(ev);
        if (result.Changed)
            PostChanged();
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

    /// <summary>Move to the text start, announcing the first line like
    /// Ctrl+Home ("Top, ..." only when already there).</summary>
    public void MoveToDocStart() => Nav(InputKind.MoveToDocStart);

    /// <summary>Move to the text end, announcing the last line like
    /// Ctrl+End ("Bottom, ..." only when already there).</summary>
    public void MoveToDocEnd() => Nav(InputKind.MoveToDocEnd);

    // ── Undo ──

    /// <summary>Undo the most recent edit, announcing like Ctrl+Z: the
    /// value at the restored state — the selection that came back, or
    /// the current line — or "Nothing to undo". One undo step is a
    /// typing sequence or one bulk operation (paste, cut, a selection
    /// delete or replace, a word delete, InsertText, ReplaceRange). A
    /// typing sequence ends only when the cursor is not where typing
    /// left it as the next edit arrives, or when ten seconds pass
    /// between edits; backspace and delete ride in the sequence.
    /// Setting <see cref="Text"/> clears the history.</summary>
    public void Undo() => Nav(InputKind.Undo);

    /// <summary>Redo the most recently undone edit, announcing like
    /// Ctrl+Y. Any new edit discards the redo steps.</summary>
    public void Redo() => Nav(InputKind.Redo);

    /// <summary>Undo memory: the retained-character budget (UTF-16 code
    /// units of removed plus inserted text summed over held undo
    /// steps). Past it the oldest steps are evicted; the newest is
    /// always kept, even alone over budget. A smaller budget applies
    /// from the next edit.</summary>
    public int UndoMemory
    {
        get => _editor.History.MaxChars;
        set => _editor.History.MaxChars = Math.Max(value, 0);
    }

    // ── Position queries ──
    // Pure reads over the text engine: no state changes, no announcements.
    // Positions clamp to the text and snap onto grapheme boundaries, like
    // the position setters. Lines and columns are 0-based; a column is
    // the UTF-16 code-unit offset from its line start.

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
    /// and the result snaps onto a grapheme boundary.</summary>
    public int PositionAt(int line, int column) =>
        Snap(TextNav.PositionOfLineColumn(_editor.Rope, Math.Max(line, 0), Math.Max(column, 0)));

    /// <summary>Clamp a position to the text and snap it backward onto a
    /// grapheme cluster boundary — never inside a surrogate pair, a
    /// combining sequence, a CRLF, or an emoji ZWJ sequence.</summary>
    private int Snap(int position) => TextNav.SnapToGraphemeBoundary(_editor.Rope, position);

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
        // Ctrl+clipboard/select-all/undo/redo.
        if (combo.Ctrl && !combo.Alt && !combo.Shift && combo.Key.IsChar(out var c)
            && c is 'c' or 'x' or 'v' or 'a' or 'z' or 'y') return true;
        // Ctrl+Shift+Z (redo).
        if (combo.Ctrl && !combo.Alt && combo.Shift
            && combo.Key.IsChar(out var rz) && rz == 'z') return true;
        return base.ReservesKey(combo);
    }

    protected override bool OnInput(in InputEvent input)
    {
        var result = EditBoxCore.Handle(this, input, _editor, Engine.Clipboard, NowMs);
        if (!result.Consumed)
            return false;
        Quiet();
        foreach (var ev in result.Events)
            Promulgate(ev);
        if (result.Changed)
            PostChanged();
        return true;
    }
}
