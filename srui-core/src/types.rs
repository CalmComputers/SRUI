//! Golden six types — the semantic properties of every widget.

use bitflags::bitflags;
use std::fmt;

use crate::key_combo::{Key, KeyCombo};

/// Widget role — what kind of control this is.
///
/// Most roles are simple unit variants. `EditBox` carries semantic flags
/// (`read_only`, `multiline`) because screen readers speak them as part of
/// the role — "edit read only multi line" — not as separate states.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum Role {
    Button,
    CheckBox,
    EditBox { read_only: bool, multiline: bool },
    ListBox,
    Group,
    Label,
    TabControl,
    ShortcutField,
    SearchField,
    Slider,
}

impl Role {
    /// Shorthand for a default (single-line, editable) edit box.
    pub const fn edit() -> Self {
        Role::EditBox {
            read_only: false,
            multiline: false,
        }
    }

    /// Whether this widget type might consume the given key combo during normal
    /// interaction. Used for shortcut conflict detection.
    pub fn reserves_key(&self, combo: &KeyCombo) -> bool {
        match self {
            Role::Button | Role::CheckBox => {
                // Activate (Enter) and Space, no modifiers
                !combo.ctrl && !combo.alt && !combo.shift
                    && matches!(combo.key, Key::Enter | Key::Space)
            }

            Role::EditBox { .. } => {
                // Unmodified printable chars and Space
                if !combo.ctrl && !combo.alt && !combo.shift {
                    match combo.key {
                        Key::Char(_) | Key::Space | Key::Enter => return true,
                        Key::Up | Key::Down | Key::Left | Key::Right => return true,
                        Key::Home | Key::End => return true,
                        Key::Backspace | Key::Delete => return true,
                        _ => {}
                    }
                }
                // Shift+movement (selection)
                if combo.shift && !combo.ctrl && !combo.alt {
                    match combo.key {
                        Key::Left | Key::Right | Key::Up | Key::Down
                        | Key::Home | Key::End => return true,
                        _ => {}
                    }
                }
                // Ctrl+movement/editing
                if combo.ctrl && !combo.alt {
                    match combo.key {
                        Key::Left | Key::Right | Key::Home | Key::End => return true,
                        Key::Backspace | Key::Delete => return true,
                        _ => {}
                    }
                }
                // Ctrl+clipboard/select
                if combo.ctrl && !combo.alt && !combo.shift {
                    match combo.key {
                        Key::Char('c') | Key::Char('x') | Key::Char('v')
                        | Key::Char('a') => return true,
                        _ => {}
                    }
                }
                // Ctrl+Shift+selection
                if combo.ctrl && combo.shift && !combo.alt {
                    match combo.key {
                        Key::Left | Key::Right | Key::Home | Key::End => return true,
                        _ => {}
                    }
                }
                false
            }

            Role::ListBox => {
                if !combo.ctrl && !combo.alt && !combo.shift {
                    match combo.key {
                        Key::Up | Key::Down | Key::Home | Key::End | Key::Enter => true,
                        Key::Char(_) | Key::Space => true, // type-ahead
                        Key::Backspace => true,             // filter mode
                        _ => false,
                    }
                } else {
                    false
                }
            }

            Role::TabControl => {
                !combo.ctrl && !combo.alt && !combo.shift
                    && matches!(combo.key, Key::Left | Key::Right | Key::Up | Key::Down | Key::Home | Key::End)
            }

            // ShortcutField captures any keypress as the shortcut value
            Role::ShortcutField => true,

            Role::SearchField => {
                // Union of edit typing + list navigation keys
                if !combo.ctrl && !combo.alt && !combo.shift {
                    match combo.key {
                        Key::Char(_) | Key::Space => return true,
                        Key::Backspace | Key::Delete => return true,
                        Key::Up | Key::Down | Key::Left | Key::Right => return true,
                        Key::Home | Key::End => return true,
                        Key::Enter => return true,
                        _ => {}
                    }
                }
                // Ctrl+word navigation and word delete
                if combo.ctrl && !combo.alt && !combo.shift {
                    match combo.key {
                        Key::Left | Key::Right => return true,
                        Key::Backspace | Key::Delete => return true,
                        _ => {}
                    }
                }
                false
            }

            Role::Slider => {
                if !combo.ctrl && !combo.alt {
                    match combo.key {
                        Key::Left | Key::Right | Key::Up | Key::Down => return true,
                        Key::Home | Key::End | Key::PageUp | Key::PageDown => return true,
                        _ => {}
                    }
                }
                false
            }

            Role::Group | Role::Label => false,
        }
    }

    /// Check variant identity, ignoring EditBox flags.
    pub fn matches_role(&self, other: &Role) -> bool {
        match (self, other) {
            (Role::EditBox { .. }, Role::EditBox { .. }) => true,
            _ => std::mem::discriminant(self) == std::mem::discriminant(other),
        }
    }
}

impl fmt::Display for Role {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Role::Button => write!(f, "button"),
            Role::CheckBox => write!(f, "check box"),
            Role::EditBox {
                read_only,
                multiline,
            } => {
                write!(f, "edit")?;
                if *read_only {
                    write!(f, " read only")?;
                }
                if *multiline {
                    write!(f, " multi line")?;
                }
                Ok(())
            }
            Role::ListBox => write!(f, "list"),
            Role::Group => write!(f, "group"),
            Role::Label => write!(f, "label"),
            Role::TabControl => write!(f, "tab control"),
            Role::ShortcutField => write!(f, "shortcut field"),
            Role::SearchField => write!(f, "search"),
            Role::Slider => write!(f, "slider"),
        }
    }
}

bitflags! {
    /// Widget states — conditions that cannot be directly changed by the user.
    #[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
    pub struct States: u32 {
        const FOCUSED  = 1 << 0;
        const DISABLED = 1 << 1;
        const REQUIRED = 1 << 2;
        const WARNING  = 1 << 3;
        const HIDDEN   = 1 << 5;
    }
}

/// The six semantic properties of every widget.
///
/// Follows NVDA announcement ordering:
/// Name Role Value States Description Shortcut
///
/// `name` is `Option<String>` because a small number of widgets have no
/// user-facing name and announce as "role value" only (e.g. an edit box
/// inside a container whose name already carries the identity). Identity
/// (for focus tracking) is the NodeId, not the name — the name is purely
/// for speech.
#[derive(Debug, Clone, PartialEq)]
pub struct WidgetLabel {
    pub name: Option<String>,
    pub role: Role,
    pub value: String,
    pub states: States,
    /// Dynamic state text spoken before bitflag states (e.g. "filter ed", "no filter").
    pub state_text: String,
    pub description: String,
    pub shortcut: Option<char>,
}

impl WidgetLabel {
    /// Construct a labeled widget.
    pub fn new(name: impl Into<String>, role: Role) -> Self {
        Self {
            name: Some(name.into()),
            role,
            value: String::new(),
            states: States::empty(),
            state_text: String::new(),
            description: String::new(),
            shortcut: None,
        }
    }

    /// Construct a widget with no user-facing name. Focus announcements
    /// will say "role value" only. Use for widgets whose identity is
    /// carried by their container.
    pub fn nameless(role: Role) -> Self {
        Self {
            name: None,
            role,
            value: String::new(),
            states: States::empty(),
            state_text: String::new(),
            description: String::new(),
            shortcut: None,
        }
    }

    /// Borrow the display name, if any.
    pub fn name_str(&self) -> Option<&str> {
        self.name.as_deref()
    }
}

/// Whether a widget can receive focus.
pub fn is_focusable(role: Role, states: States) -> bool {
    if states.contains(States::DISABLED) || states.contains(States::HIDDEN) {
        return false;
    }
    match role {
        Role::Button
        | Role::CheckBox
        | Role::EditBox { .. }
        | Role::ListBox
        | Role::TabControl
        | Role::ShortcutField
        | Role::SearchField
        | Role::Slider => true,
        Role::Group | Role::Label => false,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn role_display() {
        assert_eq!(Role::Button.to_string(), "button");
        assert_eq!(Role::CheckBox.to_string(), "check box");
        assert_eq!(Role::edit().to_string(), "edit");
        assert_eq!(
            Role::EditBox {
                read_only: true,
                multiline: false
            }
            .to_string(),
            "edit read only"
        );
        assert_eq!(
            Role::EditBox {
                read_only: false,
                multiline: true
            }
            .to_string(),
            "edit multi line"
        );
        assert_eq!(
            Role::EditBox {
                read_only: true,
                multiline: true
            }
            .to_string(),
            "edit read only multi line"
        );
        assert_eq!(Role::ListBox.to_string(), "list");
        assert_eq!(Role::Group.to_string(), "group");
        assert_eq!(Role::TabControl.to_string(), "tab control");
    }

    #[test]
    fn focusable_rules() {
        assert!(is_focusable(Role::Button, States::empty()));
        assert!(is_focusable(Role::CheckBox, States::empty()));
        assert!(is_focusable(Role::edit(), States::empty()));
        assert!(is_focusable(Role::ListBox, States::empty()));
        assert!(is_focusable(Role::TabControl, States::empty()));
        assert!(!is_focusable(Role::Group, States::empty()));
        assert!(!is_focusable(Role::Label, States::empty()));
        assert!(!is_focusable(Role::Button, States::DISABLED));
        assert!(!is_focusable(Role::Button, States::HIDDEN));
        assert!(!is_focusable(Role::CheckBox, States::HIDDEN));
    }

    #[test]
    fn states_bitflags() {
        let s = States::FOCUSED | States::DISABLED;
        assert!(s.contains(States::FOCUSED));
        assert!(s.contains(States::DISABLED));
        assert!(!s.contains(States::REQUIRED));
    }

    #[test]
    fn button_reserves_enter_and_space() {
        assert!(Role::Button.reserves_key(&KeyCombo::plain(Key::Enter)));
        assert!(Role::Button.reserves_key(&KeyCombo::plain(Key::Space)));
        assert!(!Role::Button.reserves_key(&KeyCombo::ctrl(Key::Char('s'))));
        assert!(!Role::Button.reserves_key(&KeyCombo::plain(Key::Up)));
    }

    #[test]
    fn editbox_reserves_typing_and_navigation() {
        let edit = Role::edit();
        // Typing
        assert!(edit.reserves_key(&KeyCombo::plain(Key::Char('a'))));
        assert!(edit.reserves_key(&KeyCombo::plain(Key::Space)));
        assert!(edit.reserves_key(&KeyCombo::plain(Key::Enter)));
        // Navigation
        assert!(edit.reserves_key(&KeyCombo::plain(Key::Left)));
        assert!(edit.reserves_key(&KeyCombo::ctrl(Key::Left)));
        // Selection
        assert!(edit.reserves_key(&KeyCombo::new(Key::Right, false, false, true)));
        assert!(edit.reserves_key(&KeyCombo::new(Key::Right, true, false, true)));
        // Clipboard
        assert!(edit.reserves_key(&KeyCombo::ctrl(Key::Char('c'))));
        assert!(edit.reserves_key(&KeyCombo::ctrl(Key::Char('v'))));
        // Deletion
        assert!(edit.reserves_key(&KeyCombo::plain(Key::Backspace)));
        assert!(edit.reserves_key(&KeyCombo::ctrl(Key::Backspace)));
        // Does NOT reserve arbitrary Ctrl combos
        assert!(!edit.reserves_key(&KeyCombo::ctrl(Key::Char('s'))));
        assert!(!edit.reserves_key(&KeyCombo::ctrl(Key::Char('n'))));
    }

    #[test]
    fn listbox_reserves_navigation_and_typeahead() {
        assert!(Role::ListBox.reserves_key(&KeyCombo::plain(Key::Up)));
        assert!(Role::ListBox.reserves_key(&KeyCombo::plain(Key::Down)));
        assert!(Role::ListBox.reserves_key(&KeyCombo::plain(Key::Home)));
        assert!(Role::ListBox.reserves_key(&KeyCombo::plain(Key::Enter)));
        assert!(Role::ListBox.reserves_key(&KeyCombo::plain(Key::Char('a'))));
        assert!(Role::ListBox.reserves_key(&KeyCombo::plain(Key::Backspace)));
        assert!(!Role::ListBox.reserves_key(&KeyCombo::ctrl(Key::Char('s'))));
    }

    #[test]
    fn tabcontrol_reserves_left_right() {
        assert!(Role::TabControl.reserves_key(&KeyCombo::plain(Key::Left)));
        assert!(Role::TabControl.reserves_key(&KeyCombo::plain(Key::Right)));
        assert!(Role::TabControl.reserves_key(&KeyCombo::plain(Key::Home)));
        assert!(Role::TabControl.reserves_key(&KeyCombo::plain(Key::Up)));
        assert!(Role::TabControl.reserves_key(&KeyCombo::plain(Key::Down)));
        assert!(!Role::TabControl.reserves_key(&KeyCombo::ctrl(Key::Char('s'))));
    }

    #[test]
    fn group_and_label_reserve_nothing() {
        let ctrl_s = KeyCombo::ctrl(Key::Char('s'));
        assert!(!Role::Group.reserves_key(&ctrl_s));
        assert!(!Role::Label.reserves_key(&ctrl_s));
        assert!(!Role::Group.reserves_key(&KeyCombo::plain(Key::Enter)));
        assert!(!Role::Label.reserves_key(&KeyCombo::plain(Key::Enter)));
    }

    #[test]
    fn matches_role_ignores_editbox_flags() {
        let edit1 = Role::edit();
        let edit2 = Role::EditBox {
            read_only: true,
            multiline: true,
        };
        assert!(edit1.matches_role(&edit2));
        assert!(edit2.matches_role(&edit1));
        assert!(!Role::Button.matches_role(&Role::CheckBox));
        assert!(Role::Button.matches_role(&Role::Button));
    }
}
