namespace Srui;

/// <summary>A list whose items fall into groups, each a run of items
/// side by side: a list of formulas by what they make, a palette's
/// commands by where they belong. Up and Down move an item at a time as
/// in any list, across the groups; Right goes to the next group's first
/// item and Left to the previous group's, with the list's edge said past
/// the last or the first. The group is a field of the list
/// (<see cref="Group"/>), so it is read on arriving at the list and again
/// whenever the cursor crosses into another group, and not on every
/// item. Typeahead, Home, End, and the rest are the list's own.
///
/// Which group an item is in is the program's to say
/// (<c>groupOf</c>); items of one group are expected to sit together,
/// and where they do not, each run is a group of its own for Left and
/// Right.</summary>
public partial class GroupedListBox<T> : ListBox<T> where T : Element
{
    private readonly Func<T, string> _groupOf;

    public GroupedListBox(
        IWidgetContainer parent, string name, IReadOnlyList<T> items, Func<T, string> groupOf,
        bool numbered = false, bool activateItems = false)
        : base(parent, name, items, numbered, activateItems)
    {
        _groupOf = groupOf;
    }

    /// <summary>The group of the item under the cursor, or null when the
    /// list is empty.</summary>
    [Field(Before = nameof(Fields.Value))]
    public string? Group => SelectedItem is { } item ? _groupOf(item) : null;

    /// <summary>The group the item is in.</summary>
    public string GroupOf(T item) => _groupOf(item);

    /// <summary>The first item of the run holding the index.</summary>
    private int RunStart(IReadOnlyList<T> items, int index)
    {
        var group = _groupOf(items[index]);
        while (index > 0 && _groupOf(items[index - 1]) == group)
            index--;
        return index;
    }

    /// <summary>The first item of the next run, or -1 at the last.</summary>
    private int NextRun(IReadOnlyList<T> items, int index)
    {
        var group = _groupOf(items[index]);
        for (var i = index + 1; i < items.Count; i++)
            if (_groupOf(items[i]) != group)
                return i;
        return -1;
    }

    /// <summary>The first item of the previous run, or -1 at the first.</summary>
    private int PreviousRun(IReadOnlyList<T> items, int index)
    {
        var start = RunStart(items, index);
        return start == 0 ? -1 : RunStart(items, start - 1);
    }

    protected override bool OnInput(in InputEvent input)
    {
        if (input.Kind is InputKind.MoveRight or InputKind.MoveLeft)
        {
            var items = Items;
            var at = SelectedIndex;
            if (items.Count == 0 || at < 0)
                return base.OnInput(input);
            var right = input.Kind == InputKind.MoveRight;
            var target = right ? NextRun(items, at) : PreviousRun(items, at);
            if (target < 0)
                AnnounceBoundary(right ? Boundary.Bottom : Boundary.Top);
            else
                SelectAndNotify(items, target);
            return true;
        }
        return base.OnInput(input);
    }
}

/// <summary>A group of plain-text items, for <see cref="GroupedListBox"/>.</summary>
public sealed record ListGroup(string Name, IReadOnlyList<string> Items);

/// <summary>The untyped grouped list, over <see cref="ListItem"/>s made
/// from groups of plain strings. <see cref="ListBox{T}.SelectedIndex"/>
/// counts through every group's items in order.</summary>
public class GroupedListBox : GroupedListBox<ListItem>
{
    public GroupedListBox(
        IWidgetContainer parent, string name, IReadOnlyList<ListGroup> groups,
        bool numbered = false, bool activateItems = false)
        : this(parent, name, Flatten(groups), numbered, activateItems)
    {
    }

    private GroupedListBox(
        IWidgetContainer parent, string name, (List<ListItem> Items, Dictionary<ListItem, string> Groups) flat,
        bool numbered, bool activateItems)
        : base(parent, name, flat.Items, item => flat.Groups.GetValueOrDefault(item, ""), numbered, activateItems)
    {
    }

    private static (List<ListItem>, Dictionary<ListItem, string>) Flatten(IReadOnlyList<ListGroup> groups)
    {
        var items = new List<ListItem>();
        var of = new Dictionary<ListItem, string>(ReferenceEqualityComparer.Instance);
        foreach (var group in groups)
            foreach (var text in group.Items)
            {
                var item = new ListItem(text);
                items.Add(item);
                of[item] = group.Name;
            }
        return (items, of);
    }
}
