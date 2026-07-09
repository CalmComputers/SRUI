using Srui.Core;

namespace Srui;

/// <summary>Type-to-filter list: printable characters build a fuzzy-match
/// query, Backspace erases it, arrows and Home/End navigate the filtered
/// results. Enter is not claimed (the layer's primary reads the
/// selection).</summary>
public class FilterListBox : Widget
{
    private List<string> _items;
    private string _filter = "";
    private int _selected;

    public FilterListBox(IWidgetContainer parent, string name, IReadOnlyList<string> items)
        : base(parent, name, "list")
    {
        _items = new List<string>(items);
        SyncLabel();
    }

    /// <summary>The current fuzzy query ("" for no filter).</summary>
    public string Filter => _filter;

    /// <summary>The items currently matching the filter, best match first.</summary>
    public List<string> Results => Fuzzy.FilterItems(_filter, _items);

    public string? SelectedItem
    {
        get
        {
            var filtered = Results;
            return _selected < filtered.Count ? filtered[_selected] : null;
        }
    }

    /// <summary>The full item list. Setting replaces it (the filter is
    /// kept, the selection reset) and re-announces the widget when focused
    /// and audibly changed.</summary>
    public IReadOnlyList<string> Items
    {
        get => _items;
        set
        {
            var copy = new List<string>(value);
            Engine.UpdateLabel(Node, label =>
            {
                _items = copy;
                _selected = 0;
                SyncInto(label);
            });
        }
    }

    /// <summary>Clear the filter and selection; re-announces when focused.</summary>
    public void ClearFilter() =>
        Engine.UpdateLabel(Node, label =>
        {
            _filter = "";
            _selected = 0;
            SyncInto(label);
        });

    /// <summary>Label value mirrors the selected result; state text
    /// carries the filter ("no filter" / "filter {query}").</summary>
    private void SyncLabel()
    {
        var filtered = Results;
        SetValue(_selected < filtered.Count ? filtered[_selected] : "empty");
        SetStateText(_filter.Length == 0 ? "no filter" : $"filter {_filter}");
    }

    private void SyncInto(WidgetLabel label)
    {
        var filtered = Results;
        label.Value = _selected < filtered.Count ? filtered[_selected] : "empty";
        label.StateText = _filter.Length == 0 ? "no filter" : $"filter {_filter}";
    }

    private void EmitResult(List<string> filtered, Boundary? boundary) =>
        EmitItem(filtered[_selected], (_selected, filtered.Count), boundary);

    private void SelectAndAnnounce(List<string> filtered, int index)
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
            this, _filter, filtered.Count > 0 ? filtered[0] : null, filtered.Count));
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
