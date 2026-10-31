namespace Srui;

/// <summary>One tab of a <see cref="TabControl"/>: the line the control
/// speaks and typeahead matches, and the panel it shows while it is the
/// active tab.</summary>
public sealed partial class Tab : Element
{
    /// <summary>The tab's name.</summary>
    [Field] public partial string? Value { get; set; }

    /// <summary>What the tab shows: the panel the control made for it
    /// (<see cref="TabControl.AddTab"/>), the one attached to it
    /// (<see cref="TabControl.AttachPanels"/>), or null for a tab that
    /// shows nothing.</summary>
    public Widget? Panel { get; internal set; }

    /// <summary>Whether the control made the panel, and so removes it
    /// with the tab.</summary>
    internal bool OwnsPanel { get; set; }

    public Tab(string name) => Value = name;

    public override string ToString() => Value ?? "";
}

/// <summary>A tab's panel as the control makes it: a group without a
/// name, since the tab just read already said what it holds, placed in
/// reading order after the control and the panels before it.</summary>
public sealed class TabPanel : Group
{
    internal TabPanel(Widget after) : base(after, null, null)
    {
    }
}

/// <summary>A list of tabs. It is a list in every respect - arrows in
/// either direction move with the list's edges, Home and End jump,
/// typed letters find a tab by name - and the item under the cursor is
/// the active tab, so a switch reads the tab and a focus arrival reads
/// the control with it. The control owns its panels' visibility: the
/// active tab's panel shows, the rest hide and leave the tab ring.
///
/// Tabs come and go while the control lives: <see cref="AddTab"/> and
/// <see cref="InsertTab"/> make a tab with a panel of its own, placed in
/// reading order below the control, to build the tab's widgets in;
/// <see cref="RemoveTab"/> takes the tab and that panel away. A control
/// made over a fixed set of names takes panels the program built itself
/// through <see cref="AttachPanels"/>, one per tab.</summary>
public class TabControl : ListBox<Tab>
{
    public TabControl(IWidgetContainer parent, string name)
        : base(parent, name, [])
    {
        Role = Role.Tabs;
    }

    public TabControl(IWidgetContainer parent, string name, IReadOnlyList<string> tabs, int active = 0)
        : base(parent, name, tabs.Select(t => new Tab(t)).ToList())
    {
        Role = Role.Tabs;
        if (tabs.Count != 0)
            SelectedIndex = active;
    }

    /// <summary>The tabs, in order.</summary>
    public IReadOnlyList<Tab> Tabs => Items;

    /// <summary>The active tab's index, or -1 with no tabs. Setting
    /// switches (clamped); panels follow at once.</summary>
    public int ActiveIndex
    {
        get => SelectedIndex;
        set => SelectedIndex = value;
    }

    /// <summary>The active tab, or null with no tabs. Setting switches
    /// to a tab of this control.</summary>
    public Tab? ActiveTab
    {
        get => SelectedItem;
        set => SelectedItem = value;
    }

    /// <summary>Add a tab at the end, with a panel of its own to build
    /// its widgets in (<see cref="Tab.Panel"/>).</summary>
    public Tab AddTab(string name) => InsertTab(Items.Count, name);

    /// <summary>Insert a tab at the index, with a panel of its own
    /// placed in reading order among the others. The active tab stays
    /// active; the first tab of an empty control becomes it.</summary>
    public Tab InsertTab(int index, string name)
    {
        var tabs = Items;
        if ((uint)index > (uint)tabs.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        Widget after = this;
        for (var i = index - 1; i >= 0; i--)
            if (tabs[i].Panel is { } before && !before.IsRemoved)
            {
                after = before;
                break;
            }
        var tab = new Tab(name) { Panel = new TabPanel(after), OwnsPanel = true };
        Insert(index, tab);
        return tab;
    }

    /// <summary>Remove a tab, and the panel the control made for it. An
    /// attached panel stays, hidden. Removing the active tab activates
    /// the one that takes its place, which is read if the control has
    /// the focus.</summary>
    public void RemoveTab(Tab tab)
    {
        var at = -1;
        var tabs = Items;
        for (var i = 0; i < tabs.Count; i++)
            if (ReferenceEquals(tabs[i], tab))
                at = i;
        if (at < 0)
            throw new ArgumentException("the tab is not in this control", nameof(tab));
        RemoveAt(at);
        if (tab.Panel is { } panel)
        {
            if (tab.OwnsPanel)
                panel.Remove();
            else
                panel.Hidden = true;
        }
        tab.Panel = null;
    }

    /// <summary>Attach one panel per tab, the program's own widgets,
    /// and hide every panel but the active one at once. Panels are
    /// created after the tab control, so they sit below it in tree
    /// order - the reading order a screen-reader user expects.</summary>
    public void AttachPanels(params Widget[] panels)
    {
        var tabs = Items;
        if (panels.Length != tabs.Count)
            throw new ArgumentException(
                $"{panels.Length} panels for {tabs.Count} tabs", nameof(panels));
        for (var i = 0; i < panels.Length; i++)
        {
            tabs[i].Panel = panels[i];
            tabs[i].OwnsPanel = false;
        }
        SyncPanels();
    }

    private protected override void CursorMoved(bool byInput)
    {
        // Panels are other widgets: a switch by input syncs them at
        // drain, outside dispatch - before Changed subscribers, which
        // are posted after, so handlers see the settled view. The
        // program's own switches sync at once.
        if (byInput)
            Post(SyncPanels);
        else
            SyncPanels();
    }

    private void SyncPanels()
    {
        var active = SelectedItem;
        foreach (var tab in Items)
            if (tab.Panel is { IsRemoved: false } panel && panel.Hidden != !ReferenceEquals(tab, active))
                panel.Hidden = !ReferenceEquals(tab, active);
    }
}
