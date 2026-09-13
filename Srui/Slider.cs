namespace Srui;

/// <summary>Arrows adjust by the small step, Shift+arrows and
/// PageUp/PageDown by the large step, Home/End jump to the range edges.
/// Adjustments at a range edge re-announce the clamped value. Bind
/// <see cref="Number"/> to the model and the slider writes itself.</summary>
public partial class Slider : Widget
{
    public Slider(
        IWidgetContainer parent, string name, double value, double min, double max,
        double smallStep = 1, double largeStep = 10, string? unit = null)
        : base(parent, name, Role.Slider)
    {
        Min = min;
        Max = max;
        Number = Math.Clamp(value, min, max);
        Step = smallStep;
        LargeStep = largeStep;
        Unit = unit;
    }

    /// <summary>The value. Writes clamp to the range.</summary>
    [Field] public partial double Number { get; set; }

    [Field] public partial double Min { get; set; }

    [Field] public partial double Max { get; set; }

    /// <summary>Spoken directly after the number ("%" → "50%").</summary>
    [Field] public partial string? Unit { get; set; }

    /// <summary>A write to the number lands inside the range.</summary>
    protected override void OnFieldWritten(Field field)
    {
        base.OnFieldWritten(field);
        if (ReferenceEquals(field, Fields.Number))
        {
            var clamped = Math.Clamp(Number, Min, Max);
            if (clamped != Number)
                Number = clamped;
        }
    }

    /// <summary>The arrow-key step.</summary>
    public double Step { get; set; }

    /// <summary>The Shift+arrow and Page step.</summary>
    public double LargeStep { get; set; }

    public override bool ReservesKey(KeyCombo combo)
    {
        if (!combo.Ctrl && !combo.Alt)
        {
            if (combo.Key == Key.Left || combo.Key == Key.Right
                || combo.Key == Key.Up || combo.Key == Key.Down) return true;
            if (combo.Key == Key.Home || combo.Key == Key.End
                || combo.Key == Key.PageUp || combo.Key == Key.PageDown) return true;
        }
        return base.ReservesKey(combo);
    }

    protected override bool OnInput(in InputEvent input)
    {
        var prev = Number;
        double target;
        switch (input.Kind)
        {
            case InputKind.MoveRight or InputKind.MoveUp:
                target = prev + Step;
                break;
            case InputKind.MoveLeft or InputKind.MoveDown:
                target = prev - Step;
                break;
            case InputKind.SelectRight or InputKind.SelectLineUp:
                target = prev + LargeStep;
                break;
            case InputKind.SelectLeft or InputKind.SelectLineDown:
                target = prev - LargeStep;
                break;
            case InputKind.MoveToLineStart:
                target = Min;
                break;
            case InputKind.MoveToLineEnd:
                target = Max;
                break;
            case InputKind.RawKey when (input.Mods & (Mods.Ctrl | Mods.Alt)) == 0:
                if (input.Key == Keys.PageUp)
                    target = prev + LargeStep;
                else if (input.Key == Keys.PageDown)
                    target = prev - LargeStep;
                else
                    return false;
                break;
            default:
                return false;
        }
        var next = Math.Clamp(target, Min, Max);
        if (next == prev)
        {
            // Clamped at an edge: say the number again so the key does
            // not feel dead.
            Reread(Fields.Number);
            return true;
        }
        Number = next;
        PostChanged();
        return true;
    }
}
