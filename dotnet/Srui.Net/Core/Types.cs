namespace Srui.Core;

/// <summary>Widget role — what kind of control this is. EditBox carries
/// semantic flags (read-only, multiline) because screen readers speak them
/// as part of the role — "edit read only multi line" — not as separate
/// states; the flags are meaningless for every other kind.</summary>
internal enum RoleKind
{
    Button,
    CheckBox,
    EditBox,
    ListBox,
    Group,
    Label,
    TabControl,
    ShortcutField,
    SearchField,
    Slider,
    /// <summary>A focusable widget with no spoken role and no built-in
    /// behavior: announcements carry name, value, states, description, and
    /// shortcut only, and every key falls through the core to the host's
    /// own bindings. The building block for app-defined interaction.</summary>
    Custom,
}

/// <summary>A role with its EditBox flags. Value equality includes the
/// flags; <see cref="MatchesKind"/> ignores them.</summary>
internal readonly record struct Role(RoleKind Kind, bool ReadOnly, bool Multiline)
{
    public static readonly Role Button = new(RoleKind.Button, false, false);
    public static readonly Role CheckBox = new(RoleKind.CheckBox, false, false);
    public static readonly Role ListBox = new(RoleKind.ListBox, false, false);
    public static readonly Role Group = new(RoleKind.Group, false, false);
    public static readonly Role Label = new(RoleKind.Label, false, false);
    public static readonly Role TabControl = new(RoleKind.TabControl, false, false);
    public static readonly Role ShortcutField = new(RoleKind.ShortcutField, false, false);
    public static readonly Role SearchField = new(RoleKind.SearchField, false, false);
    public static readonly Role Slider = new(RoleKind.Slider, false, false);
    public static readonly Role Custom = new(RoleKind.Custom, false, false);

    /// <summary>An edit box; parameterless form is single-line, editable.</summary>
    public static Role Edit(bool readOnly = false, bool multiline = false) =>
        new(RoleKind.EditBox, readOnly, multiline);

    /// <summary>Kind identity, ignoring EditBox flags.</summary>
    public bool MatchesKind(Role other) => Kind == other.Kind;

    /// <summary>The spoken role word(s). Empty for Custom — such widgets
    /// announce without a role word (speech skips the empty field).</summary>
    public string ToSpeech() => Kind switch
    {
        RoleKind.Button => "button",
        RoleKind.CheckBox => "check box",
        RoleKind.EditBox => (ReadOnly, Multiline) switch
        {
            (false, false) => "edit",
            (true, false) => "edit read only",
            (false, true) => "edit multi line",
            (true, true) => "edit read only multi line",
        },
        RoleKind.ListBox => "list",
        RoleKind.Group => "group",
        RoleKind.Label => "label",
        RoleKind.TabControl => "tab control",
        RoleKind.ShortcutField => "shortcut field",
        RoleKind.SearchField => "search",
        RoleKind.Slider => "slider",
        RoleKind.Custom => "",
        _ => "",
    };

    /// <summary>Whether this widget kind might consume the given key combo
    /// during normal interaction. Used for shortcut conflict detection.</summary>
    public bool ReservesKey(in KeyCombo combo)
    {
        switch (Kind)
        {
            case RoleKind.Button or RoleKind.CheckBox:
                // Activate (Enter) and Space, no modifiers.
                return !combo.Ctrl && !combo.Alt && !combo.Shift
                    && (combo.Key == Key.Enter || combo.Key == Key.Space);

            case RoleKind.EditBox:
            {
                // Unmodified typing, navigation, and deletion.
                if (!combo.Ctrl && !combo.Alt && !combo.Shift)
                {
                    if (combo.Key.IsChar(out _)) return true;
                    if (combo.Key == Key.Space || combo.Key == Key.Enter) return true;
                    if (combo.Key == Key.Up || combo.Key == Key.Down
                        || combo.Key == Key.Left || combo.Key == Key.Right) return true;
                    if (combo.Key == Key.Home || combo.Key == Key.End) return true;
                    if (combo.Key == Key.Backspace || combo.Key == Key.Delete) return true;
                }
                // Shift+movement (selection).
                if (combo.Shift && !combo.Ctrl && !combo.Alt)
                {
                    if (combo.Key == Key.Left || combo.Key == Key.Right
                        || combo.Key == Key.Up || combo.Key == Key.Down
                        || combo.Key == Key.Home || combo.Key == Key.End) return true;
                }
                // Ctrl+movement/editing.
                if (combo.Ctrl && !combo.Alt)
                {
                    if (combo.Key == Key.Left || combo.Key == Key.Right
                        || combo.Key == Key.Home || combo.Key == Key.End) return true;
                    if (combo.Key == Key.Backspace || combo.Key == Key.Delete) return true;
                }
                // Ctrl+clipboard/select-all.
                if (combo.Ctrl && !combo.Alt && !combo.Shift && combo.Key.IsChar(out var c)
                    && c is 'c' or 'x' or 'v' or 'a') return true;
                return false;
            }

            case RoleKind.ListBox:
                if (!combo.Ctrl && !combo.Alt && !combo.Shift)
                {
                    if (combo.Key == Key.Up || combo.Key == Key.Down
                        || combo.Key == Key.Home || combo.Key == Key.End
                        || combo.Key == Key.Enter) return true;
                    if (combo.Key.IsChar(out _) || combo.Key == Key.Space) return true; // type-ahead
                    if (combo.Key == Key.Backspace) return true; // filter mode
                }
                return false;

            case RoleKind.TabControl:
                return !combo.Ctrl && !combo.Alt && !combo.Shift
                    && (combo.Key == Key.Left || combo.Key == Key.Right
                        || combo.Key == Key.Up || combo.Key == Key.Down
                        || combo.Key == Key.Home || combo.Key == Key.End);

            // ShortcutField captures any keypress as the shortcut value.
            case RoleKind.ShortcutField:
                return true;

            case RoleKind.SearchField:
            {
                // Union of edit typing + list navigation keys.
                if (!combo.Ctrl && !combo.Alt && !combo.Shift)
                {
                    if (combo.Key.IsChar(out _) || combo.Key == Key.Space) return true;
                    if (combo.Key == Key.Backspace || combo.Key == Key.Delete) return true;
                    if (combo.Key == Key.Up || combo.Key == Key.Down
                        || combo.Key == Key.Left || combo.Key == Key.Right) return true;
                    if (combo.Key == Key.Home || combo.Key == Key.End) return true;
                    if (combo.Key == Key.Enter) return true;
                }
                // Ctrl+word navigation and word delete.
                if (combo.Ctrl && !combo.Alt && !combo.Shift)
                {
                    if (combo.Key == Key.Left || combo.Key == Key.Right) return true;
                    if (combo.Key == Key.Backspace || combo.Key == Key.Delete) return true;
                }
                return false;
            }

            case RoleKind.Slider:
                if (!combo.Ctrl && !combo.Alt)
                {
                    if (combo.Key == Key.Left || combo.Key == Key.Right
                        || combo.Key == Key.Up || combo.Key == Key.Down) return true;
                    if (combo.Key == Key.Home || combo.Key == Key.End
                        || combo.Key == Key.PageUp || combo.Key == Key.PageDown) return true;
                }
                return false;

            // Custom widgets have no built-in behavior; whatever the host
            // binds on them is outside the core's knowledge.
            default:
                return false;
        }
    }
}

/// <summary>Widget states — conditions the user cannot directly change.</summary>
[Flags]
internal enum States : uint
{
    None = 0,
    Focused = 1 << 0,
    Disabled = 1 << 1,
    Required = 1 << 2,
    Warning = 1 << 3,
    Hidden = 1 << 5,
}

/// <summary>A key combo attached to a widget, with what pressing it does.
/// A widget may carry any number; the first added is the one focus
/// announcements speak.</summary>
internal readonly record struct WidgetShortcut(KeyCombo Combo, ShortcutAction Action);

/// <summary>The six semantic properties of every widget, in NVDA
/// announcement order: Name Role Value States Description Shortcut.
/// Name is nullable because a small number of widgets have no user-facing
/// name and announce as "role value" only. Identity (for focus tracking)
/// is the NodeId, not the name — the name is purely for speech.</summary>
internal sealed class WidgetLabel
{
    public string? Name;
    public Role Role;
    public string Value = "";
    public States States;
    /// <summary>Dynamic state text spoken before flag states (e.g.
    /// "filter ed", "no filter").</summary>
    public string StateText = "";
    public string Description = "";
    /// <summary>Shortcuts attached to the widget (see Ui.AddShortcut).
    /// Focus announcements speak the first one.</summary>
    public List<WidgetShortcut> Shortcuts = new();

    /// <summary>A labeled widget.</summary>
    public WidgetLabel(string name, Role role)
    {
        Name = name;
        Role = role;
    }

    /// <summary>A widget with no user-facing name; focus announcements say
    /// "role value" only. Use when the container carries the identity.</summary>
    public static WidgetLabel Nameless(Role role) => new(role);

    private WidgetLabel(Role role)
    {
        Name = null;
        Role = role;
    }

    public WidgetLabel Clone()
    {
        var copy = new WidgetLabel(Role) { Name = Name };
        copy.Value = Value;
        copy.States = States;
        copy.StateText = StateText;
        copy.Description = Description;
        copy.Shortcuts = new List<WidgetShortcut>(Shortcuts);
        return copy;
    }

    /// <summary>Field-by-field equality; drives the "re-announce only when
    /// the label actually changed" rule.</summary>
    public bool ContentEquals(WidgetLabel other)
    {
        if (Name != other.Name || Role != other.Role || Value != other.Value
            || States != other.States || StateText != other.StateText
            || Description != other.Description
            || Shortcuts.Count != other.Shortcuts.Count)
            return false;
        for (var i = 0; i < Shortcuts.Count; i++)
            if (Shortcuts[i] != other.Shortcuts[i])
                return false;
        return true;
    }

    /// <summary>Whether a widget with this role and these states can
    /// receive focus.</summary>
    public static bool IsFocusable(Role role, States states)
    {
        if ((states & (States.Disabled | States.Hidden)) != 0)
            return false;
        return role.Kind is not (RoleKind.Group or RoleKind.Label);
    }
}
