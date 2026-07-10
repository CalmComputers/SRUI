namespace Srui;

/// <summary>Static text. Precedes the widget it describes in the tree;
/// its name becomes a context label for following siblings in context
/// re-announcements (dialog openings).</summary>
public class Label : Widget
{
    public Label(IWidgetContainer parent, string text)
        : base(parent, text, "label", focusable: false, isContextLabel: true)
    {
    }
}

/// <summary>A container; children are created with it as their parent.
/// Alt+Down enters it, Alt+Up leaves. Not in the tab ring.</summary>
public class Group : Widget
{
    public Group(IWidgetContainer parent, string name)
        : base(parent, name, "group", focusable: false)
    {
    }
}

/// <summary>A focusable widget with no spoken role and no built-in
/// behavior: it announces as its bare name, every key falls through to
/// the host, and interaction is whatever the app binds on it (BindKey
/// for press/release, shortcuts, host bindings) — or whatever a subclass
/// implements in OnInput. The building block for game surfaces and
/// bespoke controls; may contain children, like a Group.</summary>
public class CustomWidget : Widget
{
    public CustomWidget(IWidgetContainer parent, string name) : base(parent, name)
    {
    }
}

/// <summary>Enter or Space presses it (or Enter anywhere, as the layer's
/// primary; Escape anywhere, as the cancel).</summary>
public class Button : Widget
{
    public Button(IWidgetContainer parent, string name) : base(parent, name, "button")
    {
    }

    /// <summary>Shift+Enter on the focused button.</summary>
    public event Action? SecondaryActivated;

    protected virtual void OnSecondaryActivated() => SecondaryActivated?.Invoke();

    public override bool ReservesKey(KeyCombo combo) =>
        !combo.Ctrl && !combo.Alt && !combo.Shift
        && (combo.Key == Key.Enter || combo.Key == Key.Space);

    protected override bool OnInput(in InputEvent input)
    {
        switch (input.Kind)
        {
            case InputKind.Activate:
            case InputKind.TypeChar when (char)input.Ch == ' ':
                PostActivated();
                return true;
            case InputKind.SecondaryActivate:
                Post(OnSecondaryActivated);
                return true;
            default:
                return false;
        }
    }
}

/// <summary>Space toggles; Enter falls through to the layer's primary
/// (Windows dialog convention).</summary>
public class CheckBox : Widget
{
    private bool _checked;

    public CheckBox(IWidgetContainer parent, string name, bool isChecked = false)
        : base(parent, name, "check box")
    {
        _checked = isChecked;
    }

    /// <summary>"checked" / "not checked", pulled at announcement time.</summary>
    protected internal override string ValueText => _checked ? "checked" : "not checked";

    /// <summary>The checked state. A programmatic change while focused
    /// speaks the new value exactly as a user-driven toggle would; it does
    /// not raise Toggled (the program already knows).</summary>
    public bool Checked
    {
        get => _checked;
        set
        {
            if (value == _checked)
                return;
            _checked = value;
            if (IsFocused)
                Promulgate(new AccessibilityEvent.Toggle(this, value));
        }
    }

    /// <summary>The user toggled the box; the argument is the new state.</summary>
    public event Action<bool>? Toggled;

    protected virtual void OnToggled(bool isChecked) => Toggled?.Invoke(isChecked);

    public override bool ReservesKey(KeyCombo combo) =>
        !combo.Ctrl && !combo.Alt && !combo.Shift
        && (combo.Key == Key.Enter || combo.Key == Key.Space);

    protected override bool OnInput(in InputEvent input)
    {
        if (input.Kind == InputKind.TypeChar && (char)input.Ch == ' ')
        {
            _checked = !_checked;
            var isChecked = _checked;
            Post(() => OnToggled(isChecked));
            Promulgate(new AccessibilityEvent.Toggle(this, isChecked));
            return true;
        }
        return false;
    }
}
