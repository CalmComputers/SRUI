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
        SetValue(EditBoxCore.LabelValue(_editor));
    }

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
        set => Engine.UpdateLabel(Node, label =>
        {
            _editor.SetText(value);
            label.Value = EditBoxCore.LabelValue(_editor);
        });
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
            label.Value = EditBoxCore.LabelValue(_editor);
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
            SetValue(EditBoxCore.LabelValue(_editor));
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
                SetValue(EditBoxCore.LabelValue(_editor));
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
                SetValue(EditBoxCore.LabelValue(_editor));
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
        SetValue(EditBoxCore.LabelValue(_editor));
        if (_editor.IsEmpty || !IsFocused)
            return;
        var length = _editor.Length;
        var delta = length > SpeechRenderer.SpeakLimit
            ? $"{length} characters"
            : _editor.Text();
        Promulgate(new AccessibilityEvent.Selection(this, delta, SelectionKind.All));
    }

    /// <summary>Replace the text without any announcement — the
    /// counterpart of the <see cref="Text"/> setter for subclass input
    /// handlers, which mutate state silently and then emit what the user
    /// should hear (matching <see cref="Widget.SetValue"/>). The cursor
    /// lands at the end of the new text and the selection is cleared —
    /// positioned to continue typing.</summary>
    protected void SetTextSilently(string text)
    {
        _editor.SetText(text);
        _editor.Selection = null;
        _editor.Cursor = _editor.Length;
        _editor.PreferredColumn = null;
        SetValue(EditBoxCore.LabelValue(_editor));
    }

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
        SetValue(EditBoxCore.LabelValue(_editor));
        return true;
    }
}
