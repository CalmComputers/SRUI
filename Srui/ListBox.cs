using Srui.Core;

namespace Srui;

/// <summary>Single-selection list over typed items. Arrows move the
/// selection with boundary announcements — vertical and horizontal
/// alike, so a list whose real-world metaphor is a row (a hand of
/// cards) reads naturally without a second widget kind — Home/End
/// jump, printable characters do first-letter cycling and multi-letter
/// prefix search.
/// By default Enter is deliberately not claimed: it falls through to the
/// layer's primary widget (Windows dialog convention), which reads the
/// selection. A list whose items are themselves the things to open —
/// a file manager, a level select — opts into the Explorer convention
/// with <c>activateItems: true</c>, claiming Enter and raising
/// <see cref="Widget.Activated"/> for the selected item.
///
/// A multi-select list (<c>multiSelect: true</c>) lets the user check
/// items independently of the selection; the check lives on the item
/// (its <see cref="Fields.Checked"/> field). Enter toggles by default
/// (so it no longer reaches the layer's primary widget);
/// <c>toggleWithSpace: true</c> moves the toggle to Space instead, at
/// the cost of multi-letter typeahead (Space is no longer a typeahead
/// character).
///
/// Items are <see cref="Element"/>s: their fields are what the reader
/// speaks when the cursor lands on one, and their identity is what the
/// cursor follows. The items are the list's own (<see cref="Items"/>,
/// with <see cref="RemoveAt"/> and <see cref="Insert"/>) or the
/// program's (<see cref="BindItems"/>): either way the cursor stays on
/// its item through reorders, and when its item goes it lands on the
/// survivor at the same place, which the tick end reads.</summary>
public partial class ListBox<T> : Widget where T : Element
{
    /// <summary>Timeout for resetting the typeahead buffer (milliseconds
    /// of host time).</summary>
    private const ulong TypeAheadTimeoutMs = 400;

    private List<T> _items;
    private Func<IReadOnlyList<T>>? _source;
    private T? _selectedItem;
    private int _selectedIndex;
    private readonly bool _numbered;
    private readonly bool _activateItems;
    private readonly bool _multiSelect;
    private readonly bool _toggleWithSpace;
    private string _typeAheadBuffer = "";
    private ulong? _lastKeystrokeMs;

    /// <summary>Whether typed characters navigate by prefix (the
    /// default). Off, typed characters fall through unclaimed — for
    /// lists whose owner binds plain letters or Space as commands.</summary>
    public bool Typeahead { get; set; } = true;

    public ListBox(
        IWidgetContainer parent, string name, IReadOnlyList<T> items,
        bool numbered = false, bool activateItems = false,
        bool multiSelect = false, bool toggleWithSpace = false)
        : base(parent, name, Role.List)
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
        _multiSelect = multiSelect;
        _toggleWithSpace = toggleWithSpace;
    }

    // ── Fields ──

    /// <summary>Whether items are checked independently of the cursor.</summary>
    [Field] public bool MultiSelect => _multiSelect;

    /// <summary>How many items the list holds.</summary>
    [Field] public int Count => Items.Count;

    /// <summary>The cursor's position, when the list counts.</summary>
    [Field]
    public Position? Position
    {
        get
        {
            if (!_numbered)
                return null;
            var (items, item, index) = Resolve();
            return item is null ? null : new Position(index, items.Count);
        }
    }

    protected internal override Element? CurrentItem => SelectedItem;

    // ── Items ──

    /// <summary>The items. Stored on the list unless <see cref="BindItems"/>
    /// gave them a source; setting replaces the stored list. The cursor
    /// keeps its item when the item survives, else lands at its old
    /// place.</summary>
    public IReadOnlyList<T> Items
    {
        get => _source is { } source ? source() : _items;
        set
        {
            if (_source is not null)
                throw new InvalidOperationException("the items are bound; change them at the source");
            _items = new List<T>(value);
            Engine.Touch();
        }
    }

    /// <summary>Make the program's collection the list's items: every
    /// read asks the source, so membership follows the model with no
    /// call to the list. The structural operations then belong to the
    /// model, not the list.</summary>
    public void BindItems(Func<IReadOnlyList<T>> source)
    {
        _source = source;
        Engine.Touch();
    }

    /// <summary>Remove the item at the index. Removing the selected item
    /// leaves the cursor at the same place, on the survivor, which the
    /// tick end reads exactly as an arrow move would. Editorial
    /// feedback ("Deleted X.") is the caller's.</summary>
    public void RemoveAt(int index)
    {
        Stored();
        if ((uint)index >= (uint)_items.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        var (_, selected, at) = Resolve();
        _items.RemoveAt(index);
        if (index == at)
        {
            _selectedItem = null;
            _selectedIndex = index;
        }
        Engine.Touch();
    }

    /// <summary>Insert an item at the index. The cursor stays on its
    /// item; into an empty list, it lands on the new one.</summary>
    public void Insert(int index, T item)
    {
        Stored();
        if ((uint)index > (uint)_items.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        _items.Insert(index, item);
        Engine.Touch();
    }

    /// <summary>Append an item.</summary>
    public void Add(T item) => Insert(_items.Count, item);

    /// <summary>Replace the item at the index with a different one; the
    /// cursor lands on the replacement if it was on the old item.</summary>
    public void SetItem(int index, T item)
    {
        Stored();
        if ((uint)index >= (uint)_items.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (ReferenceEquals(_items[index], _selectedItem))
            _selectedItem = null;
        _items[index] = item;
        Engine.Touch();
    }

    private void Stored()
    {
        if (_source is not null)
            throw new InvalidOperationException("the items are bound; change them at the source");
    }

    // ── The cursor ──

    /// <summary>The current items with the cursor resolved against
    /// them: the selected item's place if it is still present, else the
    /// remembered index clamped, else nothing.</summary>
    private (IReadOnlyList<T> Items, T? Item, int Index) Resolve()
    {
        var items = Items;
        if (items.Count == 0)
        {
            _selectedItem = null;
            _selectedIndex = 0;
            return (items, null, -1);
        }
        if (_selectedItem is { } selected)
        {
            var at = IndexOf(items, selected, _selectedIndex);
            if (at >= 0)
            {
                _selectedIndex = at;
                return (items, selected, at);
            }
        }
        _selectedIndex = Math.Clamp(_selectedIndex, 0, items.Count - 1);
        _selectedItem = items[_selectedIndex];
        return (items, _selectedItem, _selectedIndex);
    }

    /// <summary>The item's place, by identity. The remembered place is
    /// tried first: nothing moved is the common case, and it makes the
    /// cursor's resolution constant rather than a scan.</summary>
    private static int IndexOf(IReadOnlyList<T> items, T item, int hint = -1)
    {
        if ((uint)hint < (uint)items.Count && ReferenceEquals(items[hint], item))
            return hint;
        for (var i = 0; i < items.Count; i++)
            if (ReferenceEquals(items[i], item))
                return i;
        return -1;
    }

    /// <summary>The selected item, or null when the list is empty.
    /// Setting moves the cursor to an item in the list.</summary>
    public T? SelectedItem
    {
        get => Resolve().Item;
        set
        {
            if (value is null)
                return;
            var at = IndexOf(Items, value);
            if (at < 0)
                throw new ArgumentException("the item is not in this list", nameof(value));
            Select(at, value);
        }
    }

    /// <summary>The selected index, or -1 when the list is empty.
    /// Setting moves the cursor (clamped).</summary>
    public int SelectedIndex
    {
        get => Resolve().Index;
        set
        {
            var items = Items;
            if (items.Count == 0)
                return;
            var target = Math.Clamp(value, 0, items.Count - 1);
            Select(target, items[target]);
        }
    }

    private void Select(int index, T item)
    {
        _selectedIndex = index;
        _selectedItem = item;
        Engine.Touch();
    }

    // ── Multi-select checked state ──

    /// <summary>Whether the item at the index is checked.</summary>
    public bool IsChecked(int index) => Items[index].Get(Fields.Checked) == true;

    /// <summary>Check or uncheck the item at the index. Throws when the
    /// item has no Checked field.</summary>
    public void SetChecked(int index, bool value)
    {
        var item = Items[index];
        if (!item.TrySet(Fields.Checked, value))
            throw new InvalidOperationException("the item has no Checked field");
        Engine.Touch();
    }

    /// <summary>The checked items, in list order.</summary>
    public IReadOnlyList<T> CheckedItems
    {
        get
        {
            var result = new List<T>();
            foreach (var item in Items)
                if (item.Get(Fields.Checked) == true)
                    result.Add(item);
            return result;
        }
    }

    /// <summary>The user checked or unchecked an item; the arguments are
    /// the item and its new state.</summary>
    public event Action<T, bool>? ItemToggled;

    protected virtual void OnItemToggled(T item, bool isChecked) =>
        ItemToggled?.Invoke(item, isChecked);

    /// <summary>Whether the user may toggle the item's checked state;
    /// return false to refuse. Gates user toggles only — the program's
    /// writes are never refused. A refused toggle speaks nothing by
    /// itself: announce the reason from the override so the key does
    /// not feel dead.</summary>
    protected virtual bool CanToggle(T item) => true;

    /// <summary>The user toggled the selected item: flip the item's
    /// field (the tick end reads it), notify the program.</summary>
    private void ToggleSelected()
    {
        if (Resolve().Item is not { } item || !CanToggle(item))
            return;
        var isChecked = !(item.Get(Fields.Checked) ?? false);
        if (!item.TrySet(Fields.Checked, isChecked))
            throw new InvalidOperationException(
                $"items of a multi-select list need a Checked field; {typeof(T).Name} has none");
        Engine.Touch();
        Post(() => OnItemToggled(item, isChecked));
    }

    // ── Typeahead ──

    /// <summary>Forget any pending typeahead prefix — for subclasses
    /// whose input handling replaced what the list is showing (a file
    /// pane entering another folder), so the next keystroke starts a
    /// fresh search instead of extending a prefix typed against the
    /// old items within the timeout.</summary>
    protected void ResetTypeahead()
    {
        _typeAheadBuffer = "";
        _lastKeystrokeMs = null;
    }

    private static string TextOf(T item) => item.Get(Fields.Value) ?? "";

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

        var (items, _, selected) = Resolve();
        var count = items.Count;
        if (cycling || _typeAheadBuffer == runeLower)
        {
            // Single char: cycle from the current position forward. The
            // only bearer is where the cursor already stands: say so.
            for (var offset = 1; offset <= count; offset++)
            {
                var idx = (selected + offset) % count;
                if (AsciiMatch.StartsWithLower(TextOf(items[idx]), runeLower))
                {
                    if (idx != selected)
                        SelectAndNotify(items, idx);
                    else
                        RereadItem();
                    break;
                }
            }
        }
        else
        {
            // Multi-letter prefix search with wraparound, current item included.
            var needle = _typeAheadBuffer;
            for (var offset = 0; offset < count; offset++)
            {
                var idx = (selected + offset) % count;
                if (AsciiMatch.StartsWithLower(TextOf(items[idx]), needle))
                {
                    if (idx != selected)
                        SelectAndNotify(items, idx);
                    else
                        RereadItem();
                    break;
                }
            }
        }
    }

    /// <summary>Selection moved by input: the tick end reads the landing;
    /// notify the program.</summary>
    private void SelectAndNotify(IReadOnlyList<T> items, int index)
    {
        Select(index, items[index]);
        PostChanged();
    }

    // ── Input ──

    public override bool ReservesKey(KeyCombo combo)
    {
        if (!combo.Ctrl && !combo.Alt && !combo.Shift)
        {
            if (combo.Key == Key.Up || combo.Key == Key.Down
                || combo.Key == Key.Left || combo.Key == Key.Right
                || combo.Key == Key.Home || combo.Key == Key.End
                || combo.Key == Key.Enter) return true;
            if (combo.Key.IsChar(out _) || combo.Key == Key.Space) return true; // type-ahead
            if (combo.Key == Key.Backspace) return true; // filter mode
        }
        return base.ReservesKey(combo);
    }

    protected override bool OnInput(in InputEvent input)
    {
        var (items, _, selected) = Resolve();
        if (items.Count == 0)
        {
            // An empty list still answers navigation — with what the
            // reader calls an empty list.
            switch (input.Kind)
            {
                case InputKind.MoveDown or InputKind.MoveUp
                    or InputKind.MoveRight or InputKind.MoveLeft
                    or InputKind.MoveToDocStart or InputKind.MoveToLineStart
                    or InputKind.MoveToDocEnd or InputKind.MoveToLineEnd:
                    Reread(Fields.Count);
                    return true;
                default:
                    return false;
            }
        }
        switch (input.Kind)
        {
            case InputKind.MoveDown or InputKind.MoveRight:
                if (selected + 1 < items.Count)
                    SelectAndNotify(items, selected + 1);
                else
                    AnnounceBoundary(Boundary.Bottom);
                return true;
            case InputKind.MoveUp or InputKind.MoveLeft:
                if (selected > 0)
                    SelectAndNotify(items, selected - 1);
                else
                    AnnounceBoundary(Boundary.Top);
                return true;
            case InputKind.MoveToDocStart or InputKind.MoveToLineStart:
                if (selected != 0)
                    SelectAndNotify(items, 0);
                return true;
            case InputKind.MoveToDocEnd or InputKind.MoveToLineEnd:
                if (selected != items.Count - 1)
                    SelectAndNotify(items, items.Count - 1);
                return true;
            case InputKind.Activate when _multiSelect && !_toggleWithSpace:
                ToggleSelected();
                return true;
            case InputKind.Activate when _activateItems:
                PostActivated();
                return true;
            case InputKind.TypeChar when _toggleWithSpace && input.IsChar(' '):
                ToggleSelected();
                return true;
            case InputKind.TypeChar when !Typeahead:
                return false;
            case InputKind.TypeChar:
                if (System.Text.Rune.IsValid((int)input.Ch))
                    HandleTypeAhead(char.ConvertFromUtf32((int)input.Ch));
                return true;
            default:
                return false;
        }
    }
}

/// <summary>The untyped list — <see cref="ListBox{T}"/> over
/// <see cref="ListItem"/> values, carrying the string convenience
/// overloads (plain strings wrap into <see cref="ListItem"/>).</summary>
public class ListBox : ListBox<ListItem>
{
    public ListBox(
        IWidgetContainer parent, string name, IReadOnlyList<ListItem> items,
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

    internal static List<ListItem> Wrap(IReadOnlyList<string> items)
    {
        var wrapped = new List<ListItem>(items.Count);
        foreach (var item in items)
            wrapped.Add(new ListItem(item));
        return wrapped;
    }

    /// <summary>Replace the items with plain strings.</summary>
    public void SetItems(IReadOnlyList<string> items) => Items = Wrap(items);

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
