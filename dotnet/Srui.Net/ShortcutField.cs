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
    }

    /// <summary>The captured combo's display form, or "blank".</summary>
    protected internal override string ValueText =>
        _combo is KeyCombo combo ? combo.DisplayName() : "blank";

    /// <summary>When false, capturing a combo produces no speech feedback
    /// (for bind dialogs that narrate on their own terms).</summary>
    public bool Echo { get; set; } = true;

    /// <summary>The captured combo, or null when blank. Setting speaks
    /// the new value when focused ("control p", "blank").</summary>
    public KeyCombo? Combo
    {
        get => _combo;
        set => Engine.UpdateLabel(Node, _ => _combo = value);
    }

    // A shortcut field captures any keypress as its value.
    public override bool ReservesKey(KeyCombo combo) => true;

    private void Capture(KeyCombo combo)
    {
        _combo = combo;
        if (Echo)
            SayValue(combo.DisplayName());
        PostChanged();
    }

    /// <summary>A shortcut field has no indexable concept, so its value
    /// changes ride ItemNav with no position.</summary>
    private void SayValue(string value) => AnnounceItem(value, null, null);

    protected override bool OnInput(in InputEvent input)
    {
        switch (input.Kind)
        {
            // Bare Delete/Backspace clears the shortcut; a modified form
            // (shift+backspace and friends) is an ordinary capturable
            // combo, distinguished by its physical provenance.
            case InputKind.DeleteBackward or InputKind.DeleteForward:
                if (KeyCombo.FromInput(input) is { } del && (del.Ctrl || del.Alt || del.Shift))
                {
                    Capture(del);
                    return true;
                }
                if (_combo is not null)
                {
                    _combo = null;
                    SayValue("blank");
                    PostChanged();
                }
                return true;

            // Let Tab, Escape, and framework inputs through so the
            // keyboard user can always leave the field. (Modified forms —
            // Ctrl+Tab, Shift+Escape — arrive as other kinds and are
            // captured like anything else.)
            case InputKind.NavigateNext or InputKind.NavigatePrev
                or InputKind.Dismiss or InputKind.SpeakFocus:
                return false;

            // Everything else with a combo is captured as it was actually
            // pressed: inputs carry their physical provenance, so
            // shift+backspace captures as shift+backspace even though it
            // and ctrl+backspace produce the same logical kind.
            default:
                if (KeyCombo.FromInput(input) is KeyCombo combo)
                    Capture(combo);
                // Inputs with no combo form are consumed silently.
                return true;
        }
    }
}
