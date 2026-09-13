namespace Srui;

/// <summary>Left/Right cycle through tabs with wraparound; the active
/// tab is the item under the cursor, so a switch reads the tab and a
/// focus arrival reads the control with it. Attach one panel per tab
/// (<see cref="AttachPanels"/>) and the control owns their visibility:
/// the active tab's panel shows, the rest hide and leave the tab ring.</summary>
public class TabControl : Widget
{
    private readonly List<ListItem> _tabs;
    private Widget[] _panels = [];
    private int _active;

    public TabControl(IWidgetContainer parent, string name, IReadOnlyList<string> tabs, int active = 0)
        : base(parent, name, Role.Tabs)
    {
        _tabs = ListBox.Wrap(tabs);
        _active = _tabs.Count == 0 ? 0 : Math.Clamp(active, 0, _tabs.Count - 1);
    }

    public IReadOnlyList<string> Tabs => _tabs.Select(t => t.Value ?? "").ToList();

    protected internal override Element? CurrentItem =>
        _active < _tabs.Count ? _tabs[_active] : null;

    /// <summary>Attach one panel per tab and hide every panel but the
    /// active one immediately; every later switch re-syncs (user-driven
    /// switches at drain time, programmatic ones synchronously). Panels
    /// are created after the tab control, so they sit below it in tree
    /// order — the reading order a screen-reader user expects.</summary>
    public void AttachPanels(params Widget[] panels)
    {
        if (panels.Length != _tabs.Count)
            throw new ArgumentException(
                $"{panels.Length} panels for {_tabs.Count} tabs", nameof(panels));
        _panels = panels;
        SyncPanels();
    }

    private void SyncPanels()
    {
        for (var i = 0; i < _panels.Length; i++)
            _panels[i].Hidden = i != _active;
    }

    /// <summary>The active tab's index. Setting switches (clamped);
    /// attached panels re-sync immediately.</summary>
    public int ActiveIndex
    {
        get => _tabs.Count == 0 ? -1 : _active;
        set
        {
            if (_tabs.Count == 0)
                return;
            var target = Math.Clamp(value, 0, _tabs.Count - 1);
            if (target == _active)
                return;
            _active = target;
            SyncPanels();
            Engine.Touch();
        }
    }

    public string? ActiveTab => _active < _tabs.Count ? _tabs[_active].Value : null;

    public override bool ReservesKey(KeyCombo combo) =>
        !combo.Ctrl && !combo.Alt && !combo.Shift
        && (combo.Key == Key.Left || combo.Key == Key.Right
            || combo.Key == Key.Up || combo.Key == Key.Down
            || combo.Key == Key.Home || combo.Key == Key.End)
        || base.ReservesKey(combo);

    protected override bool OnInput(in InputEvent input)
    {
        if (_tabs.Count == 0)
            return false;
        switch (input.Kind)
        {
            case InputKind.MoveRight:
                Switch(_active + 1 < _tabs.Count ? _active + 1 : 0);
                return true;
            case InputKind.MoveLeft:
                Switch(_active > 0 ? _active - 1 : _tabs.Count - 1);
                return true;
            default:
                return false;
        }
    }

    private void Switch(int to)
    {
        _active = to;
        Engine.Touch();
        // Panels are other widgets: touch them at drain, outside
        // dispatch — before Changed subscribers, so handlers see the
        // settled view.
        if (_panels.Length != 0)
            Post(SyncPanels);
        PostChanged();
    }
}
