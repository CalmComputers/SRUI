namespace Srui;

/// <summary>Static text. Precedes the widget it describes in the tree;
/// its name becomes a context label for following siblings in context
/// re-announcements (dialog openings).</summary>
public class Label : Widget
{
    public Label(IWidgetContainer parent, string text)
        : base(parent, text, Role.Label, focusable: false, isContextLabel: true)
    {
    }
}

/// <summary>A container; children are created with it as their parent.
/// Alt+Down enters it, Alt+Up leaves. Not in the tab ring. Focus
/// arriving from outside speaks the group first, as "name role", before
/// the widget it lands on ("Options group Word Wrap check box not
/// checked"); moves within it do not repeat it. A group with no name
/// and the default role is structure only and speaks nothing on entry;
/// another role marks a container that is a thing in its own right
/// (<c>new Role("console")</c>).</summary>
public class Group : Widget
{
    public Group(IWidgetContainer parent, string? name, Role? role = null)
        : base(parent, name, role ?? Role.Group, focusable: false)
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
    public Button(IWidgetContainer parent, string name) : base(parent, name, Role.Button)
    {
    }

    /// <summary>Shift+Enter on the focused button.</summary>
    public event Action? SecondaryActivated;

    protected virtual void OnSecondaryActivated() => SecondaryActivated?.Invoke();

    public override bool ReservesKey(KeyCombo combo) =>
        (!combo.Ctrl && !combo.Alt && !combo.Shift
            && (combo.Key == Key.Enter || combo.Key == Key.Space))
        || base.ReservesKey(combo);

    protected override bool OnInput(in InputEvent input)
    {
        switch (input.Kind)
        {
            case InputKind.Activate:
            case InputKind.TypeChar when input.IsChar(' '):
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
public partial class CheckBox : Widget
{
    public CheckBox(IWidgetContainer parent, string name, bool isChecked = false)
        : base(parent, name, Role.CheckBox)
    {
        Checked = isChecked;
    }

    /// <summary>The checked state. Bind it to the model and the box
    /// writes itself.</summary>
    [Field] public partial bool Checked { get; set; }

    /// <summary>The user toggled the box; the argument is the new state.</summary>
    public event Action<bool>? Toggled;

    protected virtual void OnToggled(bool isChecked) => Toggled?.Invoke(isChecked);

    public override bool ReservesKey(KeyCombo combo) =>
        (!combo.Ctrl && !combo.Alt && !combo.Shift
            && (combo.Key == Key.Enter || combo.Key == Key.Space))
        || base.ReservesKey(combo);

    protected override bool OnInput(in InputEvent input)
    {
        if (input.IsChar(' '))
        {
            Checked = !Checked;
            var isChecked = Checked;
            Post(() => OnToggled(isChecked));
            return true;
        }
        return false;
    }
}
