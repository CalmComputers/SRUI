using Srui.Core;

namespace Srui;

/// <summary>Type-to-filter list: printable characters build a query,
/// Backspace erases it, arrows and Home/End navigate the filtered
/// results. Enter is not claimed (the layer's primary reads the
/// selection). Matching and ranking belong to the items: each
/// <see cref="IListItem"/> scores itself against the query
/// (<see cref="IListItem.FilterScore"/> — null excludes, higher first;
/// the default is the built-in fuzzy match), so command-palette-style
/// item types can rank recency or pin entries.</summary>
public class FilterListBox : Widget
{
    private List<IListItem> _items;
    private string _filter = "";
    private int _selected;

    public FilterListBox(IWidgetContainer parent, string name, IReadOnlyList<IListItem> items)
        : base(parent, name, "list")
    {
        _items = new List<IListItem>(items);
        SyncLabel();
    }

    public FilterListBox(IWidgetContainer parent, string name, IReadOnlyList<string> items)
        : this(parent, name, Wrap(items))
    {
    }

    private static List<IListItem> Wrap(IReadOnlyList<string> items)
    {
        var wrapped = new List<IListItem>(items.Count);
        foreach (var item in items)
            wrapped.Add(new ListItem(item));
        return wrapped;
    }

    /// <summary>The current query ("" for no filter).</summary>
    public string Filter => _filter;

    /// <summary>The items currently matching the filter, best match first.</summary>
    public List<IListItem> Results => Fuzzy.FilterItems(_filter, _items);

    public IListItem? SelectedItem
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
    public IReadOnlyList<IListItem> Items
    {
        get => _items;
        set
        {
            var copy = new List<IListItem>(value);
            Engine.UpdateLabel(Node, label =>
            {
                _items = copy;
                _selected = 0;
                SyncInto(label);
            });
        }
    }

    /// <summary>Replace the item list with plain strings; equivalent to
    /// setting <see cref="Items"/>.</summary>
    public void SetItems(IReadOnlyList<string> items) => Items = Wrap(items);

    /// <summary>Clear the filter and selection; the reset selection
    /// speaks when focused and audibly changed.</summary>
    public void ClearFilter() =>
        Engine.UpdateLabel(Node, label =>
        {
            _filter = "";
            _selected = 0;
            SyncInto(label);
        });

    /// <summary>Focus announcements pull the label fresh, so item lines
    /// computed from mutated application state read correctly with no
    /// sync call.</summary>
    protected internal override void RefreshLabel() => SyncLabel();

    /// <summary>Label value mirrors the selected result; state text
    /// carries the filter ("no filter" / "filter {query}").</summary>
    private void SyncLabel()
    {
        var filtered = Results;
        SetValue(_selected < filtered.Count ? filtered[_selected].Text : "empty");
        SetStateText(_filter.Length == 0 ? "no filter" : $"filter {_filter}");
    }

    private void SyncInto(WidgetLabel label)
    {
        var filtered = Results;
        label.Value = _selected < filtered.Count ? filtered[_selected].Text : "empty";
        label.StateText = _filter.Length == 0 ? "no filter" : $"filter {_filter}";
    }

    private void EmitResult(List<IListItem> filtered, Boundary? boundary) =>
        EmitItem(filtered[_selected].Text, (_selected, filtered.Count), boundary);

    private void SelectAndAnnounce(List<IListItem> filtered, int index)
    {
        _selected = index;
        SyncLabel();
        EmitResult(filtered, null);
        NotifyChanged();
    }

    /// <summary>Filter text changed: reset the selection and report the
    /// new results.</summary>
    private void FilterChanged()
    {
        _selected = 0;
        SyncLabel();
        var filtered = Results;
        Emit(new AccessibilityEvent.Filter(
            this, _filter, filtered.Count > 0 ? filtered[0].Text : null, filtered.Count));
        NotifyChanged();
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
                    EmitResult(filtered, Boundary.Bottom);
                return true;
            case InputKind.MoveUp when filtered.Count > 0:
                if (_selected > 0)
                    SelectAndAnnounce(filtered, _selected - 1);
                else
                    EmitResult(filtered, Boundary.Top);
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
                EmitItem("empty", null, null);
                return true;
            case InputKind.TypeChar:
                if (System.Text.Rune.IsValid((int)input.Ch))
                    _filter += ListBox.AsciiLowerString(char.ConvertFromUtf32((int)input.Ch));
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
