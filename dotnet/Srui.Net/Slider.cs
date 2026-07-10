namespace Srui;

/// <summary>Arrows adjust by the small step, Shift+arrows and
/// PageUp/PageDown by the large step, Home/End jump to the range edges.
/// Adjustments at a range edge re-announce the clamped value.</summary>
public class Slider : Widget
{
    private int _value;
    private readonly int _min;
    private readonly int _max;
    private readonly int _smallStep;
    private readonly int _largeStep;
    /// <summary>Spoken and displayed immediately after the value ("%" → "50%").</summary>
    private readonly string _unit;

    public Slider(
        IWidgetContainer parent, string name, int value, int min, int max,
        int smallStep = 1, int largeStep = 10, string unit = "")
        : base(parent, name, "slider")
    {
        _min = min;
        _max = max;
        _value = Math.Clamp(value, min, max);
        _smallStep = smallStep;
        _largeStep = largeStep;
        _unit = unit;
        SetValue($"{_value}{_unit}");
    }

    public int Minimum => _min;

    public int Maximum => _max;

    /// <summary>The value (clamped to the range). A programmatic change
    /// while focused speaks the new value alone, exactly as a user-driven
    /// adjustment would — so a ticking progress slider stays terse.</summary>
    public int Value
    {
        get => _value;
        set
        {
            var clamped = Math.Clamp(value, _min, _max);
            if (clamped == _value)
                return;
            _value = clamped;
            SetValue($"{_value}{_unit}");
            if (IsFocused)
                Promulgate(new AccessibilityEvent.SliderChange(this, _value, _unit));
        }
    }

    public override bool ReservesKey(KeyCombo combo)
    {
        if (!combo.Ctrl && !combo.Alt)
        {
            if (combo.Key == Key.Left || combo.Key == Key.Right
                || combo.Key == Key.Up || combo.Key == Key.Down) return true;
            if (combo.Key == Key.Home || combo.Key == Key.End
                || combo.Key == Key.PageUp || combo.Key == Key.PageDown) return true;
        }
        return false;
    }

    protected override bool OnInput(in InputEvent input)
    {
        var prev = _value;
        int? delta;
        switch (input.Kind)
        {
            case InputKind.MoveRight or InputKind.MoveUp:
                delta = _smallStep;
                break;
            case InputKind.MoveLeft or InputKind.MoveDown:
                delta = -_smallStep;
                break;
            case InputKind.SelectRight or InputKind.SelectLineUp:
                delta = _largeStep;
                break;
            case InputKind.SelectLeft or InputKind.SelectLineDown:
                delta = -_largeStep;
                break;
            case InputKind.MoveToLineStart:
                _value = _min;
                delta = null;
                break;
            case InputKind.MoveToLineEnd:
                _value = _max;
                delta = null;
                break;
            case InputKind.RawKey when (input.Mods & (Mods.Ctrl | Mods.Alt)) == 0:
                if (input.Key == Keys.PageUp)
                    delta = _largeStep;
                else if (input.Key == Keys.PageDown)
                    delta = -_largeStep;
                else
                    return false;
                break;
            default:
                return false;
        }
        if (delta is int d)
            _value = Math.Clamp(_value + d, _min, _max);
        SetValue($"{_value}{_unit}");
        // Announce even when clamped at an edge; notify only real change.
        Promulgate(new AccessibilityEvent.SliderChange(this, _value, _unit));
        if (_value != prev)
            PostChanged();
        return true;
    }
}
