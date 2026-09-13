using Srui.Core;

namespace Srui;

/// <summary>Type-to-filter list over typed items: printable characters
/// build a query, Backspace erases it, arrows and Home/End navigate the
/// filtered results. Enter is not claimed (the layer's primary reads the
/// selection). Matching and ranking are the list's <see cref="Score"/>:
/// an item against the query, null excludes, higher first; the default
/// is the built-in fuzzy match over the item's <see cref="Fields.Value"/>,
/// and a command-palette-style list installs its own to rank recency
/// or pin entries. Plain strings arrive through the non-generic
/// <see cref="FilterListBox"/>.</summary>
public partial class FilterListBox<T> : Widget where T : Element
{
    private List<T> _items;
    private Func<IReadOnlyList<T>>? _source;
    private T? _selectedItem;
    private int _selected;

    public FilterListBox(IWidgetContainer parent, string name, IReadOnlyList<T> items)
        : base(parent, name, Role.FilterList)
    {
        _items = new List<T>(items);
        Filter = "";
    }

    /// <summary>The current query; empty for no filter.</summary>
    [Field] public partial string Filter { get; set; }

    /// <summary>How many items match the filter.</summary>
    [Field] public int Count => Results.Count;

    /// <summary>The selected result's position among the results: the
    /// whole pool unfiltered, and under a filter how much of it the
    /// query left.</summary>
    [Field]
    public Position? Position
    {
        get
        {
            var (results, item, index) = Resolve();
            return item is null ? null : new Position(index, results.Count);
        }
    }

    protected internal override Element? CurrentItem => SelectedItem;

    /// <summary>The results with the cursor resolved against them: the
    /// selected item's place if it still matches, else the remembered
    /// index clamped, else nothing.</summary>
    private (List<T> Results, T? Item, int Index) Resolve()
    {
        var results = Results;
        if (results.Count == 0)
        {
            _selectedItem = null;
            _selected = 0;
            return (results, null, -1);
        }
        if (_selectedItem is { } selected)
        {
            var at = results.IndexOf(selected);
            if (at >= 0)
            {
                _selected = at;
                return (results, selected, at);
            }
        }
        _selected = Math.Clamp(_selected, 0, results.Count - 1);
        _selectedItem = results[_selected];
        return (results, _selectedItem, _selected);
    }

    private void Select(int index, T item)
    {
        _selected = index;
        _selectedItem = item;
        Engine.Touch();
    }

    /// <summary>Back to the first result, whatever it is now.</summary>
    private void ResetCursor()
    {
        _selected = 0;
        _selectedItem = null;
        Engine.Touch();
    }

    /// <summary>Score an item against a query: null excludes, higher
    /// sorts first (ties fall back to shorter then ordinal Value). The
    /// default is the built-in fuzzy match over Value — query
    /// characters in order, with word-boundary and consecutivity
    /// bonuses (<see cref="Fuzzy"/>). Widgets do not consult scores for
    /// an empty query (all items show, list order).</summary>
    public Func<T, string, int?> Score { get; set; } =
        static (item, query) => Fuzzy.FuzzyScore(query, item.Get(Fields.Value) ?? "");

    /// <summary>The items currently matching the filter, best match first.</summary>
    public List<T> Results => Fuzzy.FilterItems(Filter ?? "", Items, Score, TextOf);

    private static string TextOf(T item) => item.Get(Fields.Value) ?? "";

    public T? SelectedItem => Resolve().Item;

    /// <summary>The full item list. Stored on the list unless
    /// <see cref="BindItems"/> gave them a source; setting replaces the
    /// stored list (the filter is kept, the selection reset).</summary>
    public IReadOnlyList<T> Items
    {
        get => _source is { } source ? source() : _items;
        set
        {
            if (_source is not null)
                throw new InvalidOperationException("the items are bound; change them at the source");
            _items = new List<T>(value);
            ResetCursor();
        }
    }

    /// <summary>Make the program's collection the list's items: every
    /// read asks the source, so the pool follows the model with no call
    /// to the list.</summary>
    public void BindItems(Func<IReadOnlyList<T>> source)
    {
        _source = source;
        Engine.Touch();
    }

    /// <summary>Clear the filter and selection.</summary>
    public void ClearFilter()
    {
        Filter = "";
        ResetCursor();
    }

    /// <summary>The filter text changed, before the results are read.
    /// Live-source subclasses override this to reshape the pool for the
    /// new filter so the tick end reads fresh results; the base does
    /// nothing.</summary>
    protected virtual void OnFilterChanged(string filter)
    {
    }

    /// <summary>Whether a filter change that matches nothing is
    /// reported ("no results"). A live-source subclass whose pool is
    /// still filling returns false — an empty set is not yet a
    /// verdict, and its completion path speaks instead. The base
    /// always reports.</summary>
    protected virtual bool ReportEmptyResults => true;

    /// <summary>The selected position within <see cref="Results"/> —
    /// for subclasses restoring selection after a pool swap reordered
    /// the results. The setter clamps.</summary>
    protected int SelectedResultIndex
    {
        get => Resolve().Index;
        set
        {
            var results = Results;
            if (results.Count == 0)
                return;
            var at = Math.Clamp(value, 0, results.Count - 1);
            Select(at, results[at]);
        }
    }

    private void SelectAndNotify(List<T> results, int index)
    {
        Select(index, results[index]);
        PostChanged();
    }

    /// <summary>Filter text changed: the cursor returns to the first
    /// result, which the tick end reads — again, when it is the same
    /// result as before, so every keystroke answers. The query itself
    /// is not echoed.</summary>
    private void FilterChanged()
    {
        OnFilterChanged(Filter);
        ResetCursor();
        Suppress(Fields.Filter);
        if (!ReportEmptyResults && Results.Count == 0)
            Suppress(Fields.Count, Fields.Position);
        else
            RereadItem();
        PostChanged();
    }

    public override bool ReservesKey(KeyCombo combo)
    {
        if (!combo.Ctrl && !combo.Alt && !combo.Shift)
        {
            if (combo.Key == Key.Up || combo.Key == Key.Down
                || combo.Key == Key.Left || combo.Key == Key.Right
                || combo.Key == Key.Home || combo.Key == Key.End
                || combo.Key == Key.Enter) return true;
            if (combo.Key.IsChar(out _) || combo.Key == Key.Space) return true; // the query
            if (combo.Key == Key.Backspace) return true; // erases the query
        }
        return base.ReservesKey(combo);
    }

    protected override bool OnInput(in InputEvent input)
    {
        var (filtered, _, selected) = Resolve();
        switch (input.Kind)
        {
            case InputKind.MoveDown or InputKind.MoveRight when filtered.Count > 0:
                if (selected + 1 < filtered.Count)
                    SelectAndNotify(filtered, selected + 1);
                else
                    AnnounceBoundary(Boundary.Bottom);
                return true;
            case InputKind.MoveUp or InputKind.MoveLeft when filtered.Count > 0:
                if (selected > 0)
                    SelectAndNotify(filtered, selected - 1);
                else
                    AnnounceBoundary(Boundary.Top);
                return true;
            case InputKind.MoveToDocStart or InputKind.MoveToLineStart when filtered.Count > 0:
                if (selected != 0)
                    SelectAndNotify(filtered, 0);
                return true;
            case InputKind.MoveToDocEnd or InputKind.MoveToLineEnd when filtered.Count > 0:
                if (selected != filtered.Count - 1)
                    SelectAndNotify(filtered, filtered.Count - 1);
                return true;
            case InputKind.MoveDown or InputKind.MoveUp
                or InputKind.MoveRight or InputKind.MoveLeft
                or InputKind.MoveToDocStart or InputKind.MoveToLineStart
                or InputKind.MoveToDocEnd or InputKind.MoveToLineEnd:
                // No results — answer with what the reader already
                // calls an empty result set.
                Reread(Fields.Count);
                return true;
            case InputKind.TypeChar:
                if (System.Text.Rune.IsValid((int)input.Ch))
                    Filter += AsciiMatch.LowerString(char.ConvertFromUtf32((int)input.Ch));
                FilterChanged();
                return true;
            case InputKind.DeleteBackward when Filter is { Length: > 0 } filter:
                // Remove one character — two units when it is astral.
                var cut = filter.Length >= 2 && char.IsLowSurrogate(filter[^1]) ? 2 : 1;
                Filter = filter[..^cut];
                FilterChanged();
                return true;
            default:
                return false;
        }
    }
}

/// <summary>The untyped filter list — <see cref="FilterListBox{T}"/>
/// over <see cref="ListItem"/> values, carrying the string convenience
/// overloads.</summary>
public class FilterListBox : FilterListBox<ListItem>
{
    public FilterListBox(IWidgetContainer parent, string name, IReadOnlyList<ListItem> items)
        : base(parent, name, items)
    {
    }

    public FilterListBox(IWidgetContainer parent, string name, IReadOnlyList<string> items)
        : this(parent, name, ListBox.Wrap(items))
    {
    }

    /// <summary>Replace the item list with plain strings; equivalent to
    /// setting <see cref="FilterListBox{T}.Items"/>.</summary>
    public void SetItems(IReadOnlyList<string> items) => Items = ListBox.Wrap(items);
}
