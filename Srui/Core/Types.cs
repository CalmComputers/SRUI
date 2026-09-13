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

/// <summary>The navigation traits the tree machinery needs: what the
/// engine itself consults to decide reachability, dispatch, and
/// shortcut matching. Nothing spoken lives here — every spoken fact is
/// a field of the owning widget, described at tick end.</summary>
internal sealed class WidgetLabel
{
    /// <summary>Whether this widget kind participates in the tab ring and
    /// focus recovery. False for labels and groups (hierarchy navigation
    /// can still land on a group). Fixed at creation.</summary>
    public bool Focusable = true;
    /// <summary>True for Label widgets: their names become context labels
    /// for following siblings in context re-announcements.</summary>
    public bool IsContextLabel;
    /// <summary>Hidden leaves navigation (with the subtree); disabled
    /// stays reachable but inert.</summary>
    public bool Hidden;
    public bool Disabled;
    /// <summary>Shortcuts attached to the widget (see Widget.AddShortcut).
    /// Focus announcements speak the first one.</summary>
    public List<WidgetShortcut> Shortcuts = new();

    public WidgetLabel(bool focusable = true, bool isContextLabel = false)
    {
        Focusable = focusable;
        IsContextLabel = isContextLabel;
    }

    /// <summary>Whether the widget can currently receive tab-ring focus:
    /// a focusable kind, not hidden. Disabled widgets stay tabbable — a
    /// keyboard-only, screen-reader-first UI keeps them discoverable
    /// ("unavailable") rather than skipping them; they are inert, not
    /// invisible.</summary>
    public bool IsFocusableNow => Focusable && !Hidden;

    /// <summary>Whether the widget can currently act: focusable now and
    /// not disabled. Gates input dispatch, key bindings, shortcuts, and
    /// primary/cancel activation.</summary>
    public bool IsInteractiveNow => IsFocusableNow && !Disabled;
}
