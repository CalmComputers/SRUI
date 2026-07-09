using Srui.Core;

namespace Srui;

/// <summary>Single-selection list. Arrows move the selection with
/// boundary announcements, Home/End jump, printable characters do
/// first-letter cycling and multi-letter prefix search. Enter is
/// deliberately not claimed: it falls through to the layer's primary
/// widget (Windows dialog convention), which reads the selection.</summary>
public class ListBox : Widget
{
    /// <summary>Timeout for resetting the typeahead buffer (milliseconds
    /// of host time).</summary>
    private const ulong TypeAheadTimeoutMs = 400;

    private List<string> _items;
    private int _selected;
    /// <summary>When true, announcements and focus state text carry "N of M".</summary>
    private readonly bool _numbered;
    private string _typeAheadBuffer = "";
    private ulong? _lastKeystrokeMs;

    public ListBox(
        IWidgetContainer parent, string name, IReadOnlyList<string> items, bool numbered = false)
        : base(parent, name, "list")
    {
        _items = new List<string>(items);
        _numbered = numbered;
        SetValue(_selected < _items.Count ? _items[_selected] : "empty");
        SyncStateText();
    }

    /// <summary>The items. Setting replaces the list (selection clamped)
    /// and re-announces the widget when focused and audibly changed.</summary>
    public IReadOnlyList<string> Items
    {
        get => _items;
        set => SetItems(value);
    }

    /// <summary>Replace the item list (selection clamped); equivalent to
    /// setting <see cref="Items"/>.</summary>
    public virtual void SetItems(IReadOnlyList<string> items)
    {
        var copy = new List<string>(items);
        Engine.UpdateLabel(Node, label =>
        {
            _items = copy;
            if (_items.Count > 0 && _selected >= _items.Count)
                _selected = _items.Count - 1;
            SyncInto(label);
        });
    }

    /// <summary>The selected index, or -1 when the list is empty. A
    /// programmatic move (clamped) while focused speaks the item exactly
    /// as a user-driven move would — the item alone, not a full
    /// re-announcement.</summary>
    public int SelectedIndex
    {
        get => _items.Count > 0 ? _selected : -1;
        set
        {
            if (_items.Count == 0)
                return;
            var target = Math.Clamp(value, 0, _items.Count - 1);
            if (target == _selected)
                return;
            _selected = target;
            SyncLabel();
            if (IsFocused)
                EmitSelected(null);
        }
    }

    public string? SelectedItem => _selected < _items.Count ? _items[_selected] : null;

    /// <summary>Keep the golden six in step with the widget state: value
    /// is the selected item (or "empty"), state text is "N of M" when
    /// numbered.</summary>
    private void SyncLabel()
    {
        SetValue(_selected < _items.Count ? _items[_selected] : "empty");
        SyncStateText();
    }

    private void SyncStateText()
    {
        if (_numbered)
            SetStateText(_selected < _items.Count ? $"{_selected + 1} of {_items.Count}" : "");
    }

    private void SyncInto(WidgetLabel label)
    {
        if (_selected < _items.Count)
        {
            label.Value = _items[_selected];
            if (_numbered)
                label.StateText = $"{_selected + 1} of {_items.Count}";
        }
        else
        {
            label.Value = "empty";
            if (_numbered)
                label.StateText = "";
        }
    }

    /// <summary>The selection announcement, shared between user-driven
    /// navigation and programmatic SelectedIndex moves.</summary>
    private void EmitSelected(Boundary? boundary)
    {
        if (_selected >= _items.Count)
            return;
        (int, int)? position = _numbered ? (_selected, _items.Count) : null;
        EmitItem(_items[_selected], position, boundary);
    }

    /// <summary>Selection moved by input: sync label, announce, notify
    /// the program.</summary>
    private void SelectAndAnnounce(int index)
    {
        _selected = index;
        SyncLabel();
        EmitSelected(null);
        NotifyChanged();
    }

    private void HandleTypeAhead(string runeText)
    {
        var runeLower = AsciiLowerString(runeText);

        var now = NowMs;
        var shouldReset = _lastKeystrokeMs is not ulong last
            || now - Math.Min(now, last) > TypeAheadTimeoutMs;
        // Cycling: the same letter typed repeatedly.
        var cycling = _typeAheadBuffer.Length > 0 && IsRepeatsOf(_typeAheadBuffer, runeLower);

        if (shouldReset || cycling)
            _typeAheadBuffer = "";
        _typeAheadBuffer += runeLower;
        _lastKeystrokeMs = now;

        if (cycling || _typeAheadBuffer == runeLower)
        {
            // Single char: cycle from the current position forward.
            var count = _items.Count;
            for (var offset = 1; offset <= count; offset++)
            {
                var idx = (_selected + offset) % count;
                if (StartsWithAsciiLower(_items[idx], runeLower))
                {
                    SelectAndAnnounce(idx);
                    break;
                }
            }
        }
        else
        {
            // Multi-letter prefix search with wraparound, current item included.
            var needle = _typeAheadBuffer;
            var count = _items.Count;
            for (var offset = 0; offset < count; offset++)
            {
                var idx = (_selected + offset) % count;
                if (StartsWithAsciiLower(_items[idx], needle))
                {
                    if (idx != _selected)
                        SelectAndAnnounce(idx);
                    else
                        EmitSelected(null);
                    break;
                }
            }
        }
    }

    internal static string AsciiLowerString(string s)
    {
        var chars = s.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            chars[i] = ToAsciiLower(chars[i]);
        return new string(chars);
    }

    private static bool IsRepeatsOf(string s, string unit)
    {
        if (unit.Length == 0 || s.Length % unit.Length != 0)
            return false;
        for (var i = 0; i < s.Length; i += unit.Length)
            if (string.CompareOrdinal(s, i, unit, 0, unit.Length) != 0)
                return false;
        return true;
    }

    private static bool StartsWithAsciiLower(string item, string needle)
    {
        if (item.Length < needle.Length)
            return false;
        for (var i = 0; i < needle.Length; i++)
            if (ToAsciiLower(item[i]) != needle[i])
                return false;
        return true;
    }

    private static char ToAsciiLower(char c) => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;

    public override bool ReservesKey(KeyCombo combo)
    {
        if (!combo.Ctrl && !combo.Alt && !combo.Shift)
        {
            if (combo.Key == Key.Up || combo.Key == Key.Down
                || combo.Key == Key.Home || combo.Key == Key.End
                || combo.Key == Key.Enter) return true;
            if (combo.Key.IsChar(out _) || combo.Key == Key.Space) return true; // type-ahead
            if (combo.Key == Key.Backspace) return true; // filter mode
        }
        return false;
    }

    protected override bool OnInput(in InputEvent input)
    {
        if (_items.Count == 0)
            return false;
        switch (input.Kind)
        {
            case InputKind.MoveDown:
                if (_selected + 1 < _items.Count)
                    SelectAndAnnounce(_selected + 1);
                else
                    EmitSelected(Boundary.Bottom);
                return true;
            case InputKind.MoveUp:
                if (_selected > 0)
                    SelectAndAnnounce(_selected - 1);
                else
                    EmitSelected(Boundary.Top);
                return true;
            case InputKind.MoveToDocStart or InputKind.MoveToLineStart:
                if (_selected != 0)
                    SelectAndAnnounce(0);
                return true;
            case InputKind.MoveToDocEnd or InputKind.MoveToLineEnd:
                if (_selected != _items.Count - 1)
                    SelectAndAnnounce(_items.Count - 1);
                return true;
            case InputKind.TypeChar:
                if (System.Text.Rune.IsValid((int)input.Ch))
                    HandleTypeAhead(char.ConvertFromUtf32((int)input.Ch));
                return true;
            default:
                return false;
        }
    }
}
