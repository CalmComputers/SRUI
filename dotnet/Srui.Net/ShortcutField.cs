namespace Srui;

/// <summary>Captures whatever combo the user presses as its value —
/// including Alt+arrows and mnemonics, which it claims before the
/// framework can interpret them. Delete/Backspace clear it; Tab and
/// Escape still leave the field so the keyboard user is never trapped.</summary>
public class ShortcutField : Widget
{
    private KeyCombo? _combo;

    public ShortcutField(IWidgetContainer parent, string name)
        : base(parent, name, "shortcut field")
    {
        SetValue("blank");
    }

    /// <summary>When false, capturing a combo produces no speech feedback
    /// (for bind dialogs that narrate on their own terms).</summary>
    public bool Echo { get; set; } = true;

    /// <summary>The captured combo, or null when blank. Setting
    /// re-announces when focused.</summary>
    public KeyCombo? Combo
    {
        get => _combo;
        set => Engine.UpdateLabel(Node, label =>
        {
            _combo = value;
            label.Value = value is KeyCombo combo ? combo.DisplayName() : "blank";
        });
    }

    // A shortcut field captures any keypress as its value.
    public override bool ReservesKey(KeyCombo combo) => true;

    private void Capture(KeyCombo combo)
    {
        _combo = combo;
        SetValue(combo.DisplayName());
        if (Echo)
            SayValue(combo.DisplayName());
        NotifyChanged();
    }

    /// <summary>A shortcut field has no indexable concept, so its value
    /// changes ride ItemNav with no position.</summary>
    private void SayValue(string value) => EmitItem(value, null, null);

    protected override bool OnInput(in InputEvent input)
    {
        switch (input.Kind)
        {
            // Delete/Backspace clears the shortcut.
            case InputKind.DeleteBackward or InputKind.DeleteForward:
                if (_combo is not null)
                {
                    _combo = null;
                    SetValue("blank");
                    SayValue("blank");
                    NotifyChanged();
                }
                return true;

            // RawKey — capture the combo directly.
            case InputKind.RawKey:
                Capture(KeyCombo.FromFlat(input.Key, input.Mods));
                return true;

            // Let Tab, Escape, and framework inputs through.
            case InputKind.NavigateNext or InputKind.NavigatePrev
                or InputKind.Dismiss or InputKind.SpeakFocus:
                return false;

            // Any other input with a combo mapping — capture it.
            default:
                if (KeyCombo.FromInput(input) is KeyCombo combo)
                {
                    if (combo.Key == Key.Tab || combo.Key == Key.Escape)
                        return false; // let through for navigation/dismiss
                    Capture(combo);
                    return true;
                }
                // Unknown input — consume silently.
                return true;
        }
    }
}
