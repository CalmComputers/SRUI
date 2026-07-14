using Srui.Core;

namespace Srui;

/// <summary>Single-selection list over typed items. Arrows move the
/// selection with boundary announcements, Home/End jump, printable
/// characters do first-letter cycling and multi-letter prefix search.
/// By default Enter is deliberately not claimed: it falls through to the
/// layer's primary widget (Windows dialog convention), which reads the
/// selection. A list whose items are themselves the things to open —
/// a file manager, a level select — opts into the Explorer convention
/// with <c>activateItems: true</c>, claiming Enter and raising
/// <see cref="Widget.Activated"/> for the selected item.
///
/// A multi-select list (<c>multiSelect: true</c>) announces as
/// "multi select list" and lets the user check items independently of
/// the selection: checked items speak "checked" after their text,
/// unchecked items say nothing. Enter toggles by default (so it no
/// longer reaches the layer's primary widget); <c>toggleWithSpace:
/// true</c> moves the toggle to Space instead, at the cost of
/// multi-word typeahead (Space is no longer a typeahead character).
///
/// Items are <typeparamref name="T"/> values implementing
/// <see cref="IListItem"/> — state-bearing subclasses derive from the
/// typed form (<c>class TaskListBox : ListBox&lt;TaskItem&gt;</c>) and read
/// their items back without casts; plain strings arrive through the
/// non-generic <see cref="ListBox"/>'s string overloads. The item
/// operations (<see cref="RemoveAt"/>, <see cref="Insert"/>,
/// <see cref="SetItem"/>) own the structural consequences — selection
/// clamping and what the user hears about where the selection landed;
/// editorial feedback ("Deleted X.") stays with the caller, as an
/// attributed Announce.</summary>
public class ListBox<T> : Widget where T : class, IListItem
{
    /// <summary>Timeout for resetting the typeahead buffer (milliseconds
    /// of host time).</summary>
    private const ulong TypeAheadTimeoutMs = 400;

    private List<T> _items;
    private int _selected;
    /// <summary>When true, announcements and focus state text carry "N of M".</summary>
    private readonly bool _numbered;
    /// <summary>When true, Enter is claimed and raises Activated for the
    /// selected item instead of falling through to the layer's primary.</summary>
    private readonly bool _activateItems;
    /// <summary>The checked items of a multi-select list, by reference —
    /// null on a single-select list. Structural operations keep it in
    /// step (a removed or replaced item forgets its check).</summary>
    private readonly HashSet<T>? _checked;
    /// <summary>When true (multi-select only), Space toggles instead of
    /// Enter — and no longer feeds typeahead.</summary>
    private readonly bool _toggleWithSpace;
    private string _typeAheadBuffer = "";
    private ulong? _lastKeystrokeMs;

    public ListBox(
        IWidgetContainer parent, string name, IReadOnlyList<T> items,
        bool numbered = false, bool activateItems = false,
        bool multiSelect = false, bool toggleWithSpace = false)
        : base(parent, name, multiSelect ? "multi select list" : "list")
    {
        if (toggleWithSpace && !multiSelect)
            throw new ArgumentException(
                "toggleWithSpace only applies to a multiSelect list", nameof(toggleWithSpace));
        if (multiSelect && activateItems && !toggleWithSpace)
            throw new ArgumentException(
                "a multiSelect list toggles with Enter, so it cannot also activateItems; "
                    + "use toggleWithSpace to free Enter for activation",
                nameof(activateItems));
        _items = new List<T>(items);
        _numbered = numbered;
        _activateItems = activateItems;
        if (multiSelect)
            _checked = new HashSet<T>(ReferenceEqualityComparer.Instance);
        _toggleWithSpace = toggleWithSpace;
    }

    /// <summary>Whether this is a multi-select list.</summary>
    public bool MultiSelect => _checked is not null;

    /// <summary>The items. Setting replaces the list (selection clamped)
    /// and speaks the newly selected item when focused and audibly
    /// changed.</summary>
    public IReadOnlyList<T> Items
    {
        get => _items;
        set => SetItems(value);
    }

    /// <summary>Replace the item list (selection clamped); equivalent to
    /// setting <see cref="Items"/>.</summary>
    public virtual void SetItems(IReadOnlyList<T> items)
    {
        var copy = new List<T>(items);
        Engine.UpdateLabel(Node, _ =>
        {
            _items = copy;
            _checked?.IntersectWith(copy);
            if (_items.Count > 0 && _selected >= _items.Count)
                _selected = _items.Count - 1;
        });
    }

    /// <summary>Replace the items without any announcement — the
    /// counterpart of <see cref="SetItems(IReadOnlyList{T})"/> for
    /// subclass input handlers, which mutate state silently and then
    /// emit what the user should hear. The selection is clamped; the
    /// label follows by itself.</summary>
    protected void SetItemsSilently(IReadOnlyList<T> items)
    {
        _items = new List<T>(items);
        _checked?.IntersectWith(_items);
        if (_items.Count > 0 && _selected >= _items.Count)
            _selected = _items.Count - 1;
    }

    /// <summary>Move the selection without any announcement — the
    /// counterpart of <see cref="SelectedIndex"/>'s setter for subclass
    /// input handlers that reposition as part of a larger state change
    /// and then emit what the user should hear. Clamped.</summary>
    protected void SelectSilently(int index)
    {
        if (_items.Count > 0)
            _selected = Math.Clamp(index, 0, _items.Count - 1);
    }

    /// <summary>Remove the item at the index. Removing the selected item
    /// while focused speaks the survivor the selection lands on exactly
    /// as an arrow move would — or "empty" when the last item went;
    /// removing any other item is silent (the selection kept its item,
    /// only its position shifted). Editorial feedback ("Deleted X.") is
    /// the caller's, spoken before the call.</summary>
    public void RemoveAt(int index)
    {
        if ((uint)index >= (uint)_items.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        var wasSelected = index == _selected;
        _checked?.Remove(_items[index]);
        _items.RemoveAt(index);
        if (index < _selected)
            _selected--;
        else if (_selected >= _items.Count && _selected > 0)
            _selected = _items.Count - 1;
        if (!wasSelected || !IsFocused)
            return;
        if (_items.Count == 0)
            AnnounceEmpty();
        else
            AnnounceSelected(null);
    }

    /// <summary>Insert an item at the index. Silent — the selection stays
    /// on the same item (its index shifts when inserting at or above it)
    /// and focus announcements pick the new count up automatically —
    /// except into an empty list while focused: there the selection lands
    /// on the new item, and it speaks exactly as an arrow move would.</summary>
    public void Insert(int index, T item)
    {
        if ((uint)index > (uint)_items.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        var wasEmpty = _items.Count == 0;
        _items.Insert(index, item);
        if (!wasEmpty && index <= _selected)
            _selected++;
        if (wasEmpty && IsFocused)
            AnnounceSelected(null);
    }

    /// <summary>Append an item. Silent, like <see cref="Insert(int, T)"/>.</summary>
    public void Add(T item) => Insert(_items.Count, item);

    /// <summary>Replace the item at the index with a different one.
    /// Silent: the caller speaks the delta when the user should hear one.
    /// Mutating an existing item's state needs no call at all — its Text
    /// is read live wherever the framework needs the line.</summary>
    public void SetItem(int index, T item)
    {
        if ((uint)index >= (uint)_items.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        _checked?.Remove(_items[index]);
        _items[index] = item;
    }

    // ── Multi-select checked state ──

    /// <summary>Whether the item at the index is checked. Always false on
    /// a single-select list.</summary>
    public bool IsChecked(int index)
    {
        if ((uint)index >= (uint)_items.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        return _checked?.Contains(_items[index]) ?? false;
    }

    /// <summary>Check or uncheck the item at the index programmatically.
    /// Changing the selected item's state while focused speaks the new
    /// state exactly as a user-driven toggle would; it does not raise
    /// <see cref="ItemToggled"/> (the program already knows). Throws on a
    /// single-select list.</summary>
    public void SetChecked(int index, bool value)
    {
        if (_checked is null)
            throw new InvalidOperationException("not a multi-select list");
        if ((uint)index >= (uint)_items.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        var item = _items[index];
        var changed = value ? _checked.Add(item) : _checked.Remove(item);
        if (changed && index == _selected && IsFocused)
            Promulgate(new AccessibilityEvent.Toggle(this, value));
    }

    /// <summary>The checked items, in list order. Empty on a
    /// single-select list.</summary>
    public IReadOnlyList<T> CheckedItems
    {
        get
        {
            if (_checked is null || _checked.Count == 0)
                return Array.Empty<T>();
            var result = new List<T>(_checked.Count);
            foreach (var item in _items)
                if (_checked.Contains(item))
                    result.Add(item);
            return result;
        }
    }

    /// <summary>The user checked or unchecked an item; the arguments are
    /// the item and its new state.</summary>
    public event Action<T, bool>? ItemToggled;

    protected virtual void OnItemToggled(T item, bool isChecked) =>
        ItemToggled?.Invoke(item, isChecked);

    /// <summary>The user toggled the selected item: flip, announce the new
    /// state (the same words a check box speaks), notify the program.</summary>
    private void ToggleSelected()
    {
        var item = _items[_selected];
        var isChecked = _checked!.Add(item);
        if (!isChecked)
            _checked.Remove(item);
        Promulgate(new AccessibilityEvent.Toggle(this, isChecked));
        Post(() => OnItemToggled(item, isChecked));
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
            if (IsFocused)
                AnnounceSelected(null);
        }
    }

    public T? SelectedItem => _selected < _items.Count ? _items[_selected] : null;

    /// <summary>The selected item's line (or "empty"), pulled fresh at
    /// announcement time — an item whose Text is computed from mutated
    /// application state reads correctly with no sync call.</summary>
    protected internal override string ValueText =>
        _selected < _items.Count
            ? _checked?.Contains(_items[_selected]) == true
                ? $"{_items[_selected].Text} checked"
                : _items[_selected].Text
            : "empty";

    /// <summary>"N of M" when numbered.</summary>
    protected internal override string StateText =>
        _numbered && _selected < _items.Count ? $"{_selected + 1} of {_items.Count}" : "";

    /// <summary>The selection announcement, shared between user-driven
    /// navigation and programmatic SelectedIndex moves.</summary>
    private void AnnounceSelected(Boundary? boundary)
    {
        if (_selected >= _items.Count)
            return;
        (int, int)? position = _numbered ? (_selected, _items.Count) : null;
        bool? isChecked = _checked?.Contains(_items[_selected]);
        AnnounceItem(_items[_selected].Text, position, boundary, isChecked);
    }

    /// <summary>What an empty list says — the same word its label value
    /// and focus announcement carry.</summary>
    private void AnnounceEmpty() => AnnounceItem("empty", null, null);

    /// <summary>Selection moved by input: announce, notify the program.</summary>
    private void SelectAndAnnounce(int index)
    {
        _selected = index;
        AnnounceSelected(null);
        PostChanged();
    }

    private void HandleTypeAhead(string runeText)
    {
        var runeLower = AsciiMatch.LowerString(runeText);

        var now = NowMs;
        var shouldReset = _lastKeystrokeMs is not ulong last
            || now - Math.Min(now, last) > TypeAheadTimeoutMs;
        // Cycling: the same letter typed repeatedly.
        var cycling = _typeAheadBuffer.Length > 0 && AsciiMatch.IsRepeatsOf(_typeAheadBuffer, runeLower);

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
                if (AsciiMatch.StartsWithLower(_items[idx].Text, runeLower))
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
                if (AsciiMatch.StartsWithLower(_items[idx].Text, needle))
                {
                    if (idx != _selected)
                        SelectAndAnnounce(idx);
                    else
                        AnnounceSelected(null);
                    break;
                }
            }
        }
    }

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
        return base.ReservesKey(combo);
    }

    protected override bool OnInput(in InputEvent input)
    {
        if (_items.Count == 0)
        {
            // An empty list still answers navigation — with what the
            // focus announcement already calls it.
            switch (input.Kind)
            {
                case InputKind.MoveDown or InputKind.MoveUp
                    or InputKind.MoveToDocStart or InputKind.MoveToLineStart
                    or InputKind.MoveToDocEnd or InputKind.MoveToLineEnd:
                    AnnounceEmpty();
                    return true;
                default:
                    return false;
            }
        }
        switch (input.Kind)
        {
            case InputKind.MoveDown:
                if (_selected + 1 < _items.Count)
                    SelectAndAnnounce(_selected + 1);
                else
                    AnnounceSelected(Boundary.Bottom);
                return true;
            case InputKind.MoveUp:
                if (_selected > 0)
                    SelectAndAnnounce(_selected - 1);
                else
                    AnnounceSelected(Boundary.Top);
                return true;
            case InputKind.MoveToDocStart or InputKind.MoveToLineStart:
                if (_selected != 0)
                    SelectAndAnnounce(0);
                return true;
            case InputKind.MoveToDocEnd or InputKind.MoveToLineEnd:
                if (_selected != _items.Count - 1)
                    SelectAndAnnounce(_items.Count - 1);
                return true;
            case InputKind.Activate when _checked is not null && !_toggleWithSpace:
                ToggleSelected();
                return true;
            case InputKind.Activate when _activateItems:
                PostActivated();
                return true;
            case InputKind.TypeChar when _toggleWithSpace && input.IsChar(' '):
                ToggleSelected();
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

/// <summary>The untyped list — <see cref="ListBox{T}"/> over plain
/// <see cref="IListItem"/> values, carrying the string convenience
/// overloads (plain strings wrap into <see cref="ListItem"/>).</summary>
public class ListBox : ListBox<IListItem>
{
    public ListBox(
        IWidgetContainer parent, string name, IReadOnlyList<IListItem> items,
        bool numbered = false, bool activateItems = false,
        bool multiSelect = false, bool toggleWithSpace = false)
        : base(parent, name, items, numbered, activateItems, multiSelect, toggleWithSpace)
    {
    }

    public ListBox(
        IWidgetContainer parent, string name, IReadOnlyList<string> items,
        bool numbered = false, bool activateItems = false,
        bool multiSelect = false, bool toggleWithSpace = false)
        : this(parent, name, Wrap(items), numbered, activateItems, multiSelect, toggleWithSpace)
    {
    }

    internal static List<IListItem> Wrap(IReadOnlyList<string> items)
    {
        var wrapped = new List<IListItem>(items.Count);
        foreach (var item in items)
            wrapped.Add(new ListItem(item));
        return wrapped;
    }

    /// <summary>Replace the item list with plain strings.</summary>
    public void SetItems(IReadOnlyList<string> items) => SetItems(Wrap(items));

    /// <summary>Replace the items with plain strings, silently.</summary>
    protected void SetItemsSilently(IReadOnlyList<string> items) =>
        SetItemsSilently(Wrap(items));

    /// <summary>Insert a plain-text item at the index.</summary>
    public void Insert(int index, string item) => Insert(index, new ListItem(item));

    /// <summary>Append a plain-text item.</summary>
    public void Add(string item) => Add(new ListItem(item));

    /// <summary>Replace the item at the index with plain text.</summary>
    public void SetItem(int index, string item) => SetItem(index, new ListItem(item));
}

/// <summary>ASCII-lowercase matching for list type-ahead: matching is
/// case-insensitive in ASCII only, deliberately locale-free.</summary>
internal static class AsciiMatch
{
    internal static string LowerString(string s)
    {
        var chars = s.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            chars[i] = ToLower(chars[i]);
        return new string(chars);
    }

    internal static bool IsRepeatsOf(string s, string unit)
    {
        if (unit.Length == 0 || s.Length % unit.Length != 0)
            return false;
        for (var i = 0; i < s.Length; i += unit.Length)
            if (string.CompareOrdinal(s, i, unit, 0, unit.Length) != 0)
                return false;
        return true;
    }

    internal static bool StartsWithLower(string item, string needle)
    {
        if (item.Length < needle.Length)
            return false;
        for (var i = 0; i < needle.Length; i++)
            if (ToLower(item[i]) != needle[i])
                return false;
        return true;
    }

    private static char ToLower(char c) => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
}
