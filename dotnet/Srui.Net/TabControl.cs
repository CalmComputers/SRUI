namespace Srui;

/// <summary>Left/Right cycle through tabs with wraparound. The change
/// echo speaks the tab name alone; position and role ride the focus
/// announcement.</summary>
public class TabControl : Widget
{
    private readonly List<string> _tabs;
    private int _active;

    public TabControl(IWidgetContainer parent, string name, IReadOnlyList<string> tabs, int active = 0)
        : base(parent, name, "tab control")
    {
        _tabs = new List<string>(tabs);
        _active = _tabs.Count == 0 ? 0 : Math.Clamp(active, 0, _tabs.Count - 1);
        if (_active < _tabs.Count)
            SetValue(_tabs[_active]);
    }

    public IReadOnlyList<string> Tabs => _tabs;

    /// <summary>The active tab's index. A programmatic switch (clamped)
    /// while focused speaks the tab name alone, exactly as a user-driven
    /// switch would.</summary>
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
            SetValue(_tabs[_active]);
            if (IsFocused)
                Emit(new AccessibilityEvent.TabChange(this, _tabs[_active], (_active, _tabs.Count)));
        }
    }

    public string? ActiveTab => _active < _tabs.Count ? _tabs[_active] : null;

    public override bool ReservesKey(KeyCombo combo) =>
        !combo.Ctrl && !combo.Alt && !combo.Shift
        && (combo.Key == Key.Left || combo.Key == Key.Right
            || combo.Key == Key.Up || combo.Key == Key.Down
            || combo.Key == Key.Home || combo.Key == Key.End);

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
        SetValue(_tabs[_active]);
        Emit(new AccessibilityEvent.TabChange(this, _tabs[_active], (_active, _tabs.Count)));
        NotifyChanged();
    }
}
