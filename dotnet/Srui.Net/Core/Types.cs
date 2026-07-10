namespace Srui.Core;

/// <summary>A node handle. Zero means "no node". Engine-internal: the
/// public surface addresses widgets by object reference; handles exist so
/// the tree, focus, and queued events can name nodes without keeping
/// removed subtrees alive.</summary>
internal readonly record struct NodeId(ulong Value)
{
    public static readonly NodeId None = new(0);
    public bool IsNone => Value == 0;
}

/// <summary>A key combo attached to a widget, with what pressing it does.
/// A widget may carry any number; the first added is the one focus
/// announcements speak.</summary>
internal readonly record struct WidgetShortcut(KeyCombo Combo, ShortcutAction Action);

/// <summary>The six semantic properties of every widget, in NVDA
/// announcement order: Name Role Value States Description Shortcut — plus
/// the navigation traits the tree machinery needs (Focusable,
/// IsContextLabel). Role is carried as its spoken text; what a role
/// reserves during interaction is the owning widget's affair
/// (<see cref="Srui.Widget.ReservesKey"/>). Name is nullable because a
/// small number of widgets have no user-facing name and announce as
/// "role value" only.</summary>
internal sealed class WidgetLabel
{
    public string? Name;
    /// <summary>Spoken role text ("button", "edit read only"). Empty for
    /// role-less widgets — speech skips the empty field.</summary>
    public string RoleText = "";
    public string Value = "";
    public WidgetStates States;
    /// <summary>Dynamic state text spoken before flag states (e.g.
    /// "filter ed", "no filter").</summary>
    public string StateText = "";
    public string Description = "";
    /// <summary>Shortcuts attached to the widget (see Widget.AddShortcut).
    /// Focus announcements speak the first one.</summary>
    public List<WidgetShortcut> Shortcuts = new();
    /// <summary>Whether this widget kind participates in the tab ring and
    /// focus recovery. False for labels and groups (hierarchy navigation
    /// can still land on a group). Fixed at creation.</summary>
    public bool Focusable = true;
    /// <summary>True for Label widgets: their names become context labels
    /// for following siblings in context re-announcements.</summary>
    public bool IsContextLabel;

    public WidgetLabel(string? name, string roleText)
    {
        Name = name;
        RoleText = roleText;
    }

    /// <summary>The public golden-six snapshot, taken at event emission.</summary>
    public WidgetInfo ToInfo()
    {
        var shortcuts = Shortcuts.Count == 0
            ? EmptyShortcuts
            : Shortcuts.Select(s => s.Combo).ToArray();
        return new WidgetInfo(Name, RoleText, Value, StateText, States, Description, shortcuts);
    }

    private static readonly KeyCombo[] EmptyShortcuts = [];

    public WidgetLabel Clone()
    {
        var copy = new WidgetLabel(Name, RoleText)
        {
            Value = Value,
            States = States,
            StateText = StateText,
            Description = Description,
            Shortcuts = new List<WidgetShortcut>(Shortcuts),
            Focusable = Focusable,
            IsContextLabel = IsContextLabel,
        };
        return copy;
    }

    /// <summary>Whether the widget can currently receive tab-ring focus:
    /// a focusable kind, not hidden. Disabled widgets stay tabbable — a
    /// keyboard-only, screen-reader-first UI keeps them discoverable
    /// ("unavailable") rather than skipping them; they are inert, not
    /// invisible.</summary>
    public bool IsFocusableNow =>
        Focusable && (States & WidgetStates.Hidden) == 0;

    /// <summary>Whether the widget can currently act: focusable now and
    /// not disabled. Gates input dispatch, key bindings, shortcuts, and
    /// primary/cancel activation.</summary>
    public bool IsInteractiveNow =>
        IsFocusableNow && (States & WidgetStates.Disabled) == 0;
}
