using Srui.Core;

namespace Srui;

/// <summary>Type-to-filter list over typed items: printable characters
/// build a query, Backspace erases it, arrows and Home/End navigate the
/// filtered results. Enter is not claimed (the layer's primary reads the
/// selection). Matching and ranking belong to the items: each
/// <typeparamref name="T"/> scores itself against the query
/// (<see cref="IListItem.FilterScore"/> — null excludes, higher first;
/// the default is the built-in fuzzy match), so command-palette-style
/// item types can rank recency or pin entries. Plain strings arrive
/// through the non-generic <see cref="FilterListBox"/>.</summary>
public class FilterListBox<T> : Widget where T : class, IListItem
{
    private List<T> _items;
    private string _filter = "";
    private int _selected;

    public FilterListBox(IWidgetContainer parent, string name, IReadOnlyList<T> items)
        : base(parent, name, "list")
    {
        _items = new List<T>(items);
    }

    /// <summary>The current query ("" for no filter).</summary>
    public string Filter => _filter;

    /// <summary>The items currently matching the filter, best match first.</summary>
    public List<T> Results => Fuzzy.FilterItems(_filter, _items);

    public T? SelectedItem
    {
        get
        {
            var filtered = Results;
            return _selected < filtered.Count ? filtered[_selected] : null;
        }
    }

    /// <summary>The full item list. Setting replaces it (the filter is
    /// kept, the selection reset) and speaks the newly selected result
    /// when focused and audibly changed.</summary>
    public IReadOnlyList<T> Items
    {
        get => _items;
        set
        {
            var copy = new List<T>(value);
            Engine.UpdateLabel(Node, _ =>
            {
                _items = copy;
                _selected = 0;
            });
        }
    }

    /// <summary>Clear the filter and selection; the reset selection
    /// speaks when focused and audibly changed.</summary>
    public void ClearFilter() =>
        Engine.UpdateLabel(Node, _ =>
        {
            _filter = "";
            _selected = 0;
        });

    /// <summary>Replace the item list without any announcement — for
    /// subclass handlers that reshape base-owned state mid-dispatch,
    /// where the enclosing input flow speaks (see
    /// <see cref="ListBox{T}.SetItemsSilently"/>). The selection is
    /// clamped, not reset.</summary>
    protected void SetItemsSilently(IReadOnlyList<T> items)
    {
        _items = new List<T>(items);
        var filtered = Results;
        if (filtered.Count > 0 && _selected >= filtered.Count)
            _selected = filtered.Count - 1;
    }

    /// <summary>The filter text changed, before the new results are
    /// reported. Live-source subclasses override this to reshape
    /// <see cref="Items"/> for the new filter (via
    /// <see cref="SetItemsSilently"/>) so the report reads the fresh
    /// results; the base does nothing.</summary>
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
    /// for subclasses restoring selection identity after a silent item
    /// swap reordered the results. The setter clamps and says nothing;
    /// user-driven selection speaks through navigation as always.</summary>
    protected int SelectedResultIndex
    {
        get => _selected;
        set => _selected = Math.Clamp(value, 0, Math.Max(0, Results.Count - 1));
    }

    /// <summary>The selected result's line (or "empty"), pulled fresh at
    /// announcement time — item lines computed from mutated application
    /// state read correctly with no sync call.</summary>
    protected internal override string ValueText
    {
        get
        {
            var filtered = Results;
            return _selected < filtered.Count ? filtered[_selected].Text : "empty";
        }
    }

    /// <summary>The filter ("no filter" / "filter {query}").</summary>
    protected internal override string StateText =>
        _filter.Length == 0 ? "no filter" : $"filter {_filter}";

    private void AnnounceResult(List<T> filtered, Boundary? boundary) =>
        AnnounceItem(filtered[_selected].Text, (_selected, filtered.Count), boundary);

    private void SelectAndAnnounce(List<T> filtered, int index)
    {
        _selected = index;
        AnnounceResult(filtered, null);
        PostChanged();
    }

    /// <summary>Filter text changed: reset the selection and report the
    /// new results.</summary>
    private void FilterChanged()
    {
        OnFilterChanged(_filter);
        _selected = 0;
        var filtered = Results;
        if (filtered.Count > 0 || ReportEmptyResults)
            Promulgate(new AccessibilityEvent.Filter(
                this, _filter, filtered.Count > 0 ? filtered[0].Text : null, filtered.Count));
        PostChanged();
    }

    public override bool ReservesKey(KeyCombo combo)
    {
        if (!combo.Ctrl && !combo.Alt && !combo.Shift)
        {
            if (combo.Key == Key.Up || combo.Key == Key.Down
                || combo.Key == Key.Home || combo.Key == Key.End
                || combo.Key == Key.Enter) return true;
            if (combo.Key.IsChar(out _) || combo.Key == Key.Space) return true; // the query
            if (combo.Key == Key.Backspace) return true; // erases the query
        }
        return false;
    }

    protected override bool OnInput(in InputEvent input)
    {
        var filtered = Results;
        switch (input.Kind)
        {
            case InputKind.MoveDown when filtered.Count > 0:
                if (_selected + 1 < filtered.Count)
                    SelectAndAnnounce(filtered, _selected + 1);
                else
                    AnnounceResult(filtered, Boundary.Bottom);
                return true;
            case InputKind.MoveUp when filtered.Count > 0:
                if (_selected > 0)
                    SelectAndAnnounce(filtered, _selected - 1);
                else
                    AnnounceResult(filtered, Boundary.Top);
                return true;
            case InputKind.MoveToDocStart or InputKind.MoveToLineStart when filtered.Count > 0:
                if (_selected != 0)
                    SelectAndAnnounce(filtered, 0);
                return true;
            case InputKind.MoveToDocEnd or InputKind.MoveToLineEnd when filtered.Count > 0:
                if (_selected != filtered.Count - 1)
                    SelectAndAnnounce(filtered, filtered.Count - 1);
                return true;
            case InputKind.MoveDown or InputKind.MoveUp
                or InputKind.MoveToDocStart or InputKind.MoveToLineStart
                or InputKind.MoveToDocEnd or InputKind.MoveToLineEnd:
                // No results — answer with what the label already says.
                AnnounceItem("empty", null, null);
                return true;
            case InputKind.TypeChar:
                if (System.Text.Rune.IsValid((int)input.Ch))
                    _filter += AsciiMatch.LowerString(char.ConvertFromUtf32((int)input.Ch));
                FilterChanged();
                return true;
            case InputKind.DeleteBackward when _filter.Length > 0:
                // Remove one character — two units when it is astral.
                var cut = _filter.Length >= 2 && char.IsLowSurrogate(_filter[^1]) ? 2 : 1;
                _filter = _filter[..^cut];
                FilterChanged();
                return true;
            default:
                return false;
        }
    }
}

/// <summary>The untyped filter list — <see cref="FilterListBox{T}"/>
/// over plain <see cref="IListItem"/> values, carrying the string
/// convenience overloads.</summary>
public class FilterListBox : FilterListBox<IListItem>
{
    public FilterListBox(IWidgetContainer parent, string name, IReadOnlyList<IListItem> items)
        : base(parent, name, items)
    {
    }

    public FilterListBox(IWidgetContainer parent, string name, IReadOnlyList<string> items)
        : this(parent, name, ListBox.Wrap(items))
    {
    }

    /// <summary>Replace the item list with plain strings; equivalent to
    /// setting <see cref="Items"/>.</summary>
    public void SetItems(IReadOnlyList<string> items) => Items = ListBox.Wrap(items);
}
