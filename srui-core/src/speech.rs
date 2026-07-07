//! Speech rendering — the reference rendering of accessibility events.
//!
//! Pure functions from structured events to utterance strings. The
//! self-voicing reader uses these; braille and UIA readers ignore them
//! and read the structured payloads directly. Renderings for widgets the
//! core does not yet ship (editor text, filters) are provisional and will
//! be finalized alongside those widgets and their tests.

use crate::events::{AccessibilityEvent, Boundary, ClipboardOp, SelectionKind, TypingKind};
use crate::types::{States, WidgetLabel};

/// Maximum characters to announce verbatim for selections; beyond this we say
/// "N characters selected".
pub const SPEAK_LIMIT: usize = 500;

/// Render an accessibility event to an utterance. Returns `None` when
/// there is nothing sensible to say (readers skip silently).
pub fn render_event(event: &AccessibilityEvent) -> Option<String> {
    match event {
        AccessibilityEvent::Focused {
            label,
            context_labels,
            ..
        } => {
            let announcement = announce_focus(label);
            if context_labels.is_empty() {
                Some(announcement)
            } else {
                Some(format!("{} {}", context_labels.join(" "), announcement))
            }
        }

        AccessibilityEvent::Typing {
            grapheme,
            last_word,
            kind,
            ..
        } => match kind {
            // On a word boundary the just-completed word is echoed before
            // the separator: "hello space". For deletes, `grapheme` is
            // already in spoken form (speak_char passes multi-char strings
            // through unchanged, so re-rendering is harmless).
            TypingKind::Insert => {
                let char_speech = speak_char(grapheme);
                match last_word {
                    Some(word) => Some(format!("{word} {char_speech}")),
                    None if !grapheme.is_empty() => Some(char_speech),
                    None => None,
                }
            }
            TypingKind::Delete if !grapheme.is_empty() => Some(speak_char(grapheme)),
            TypingKind::Delete => None,
            TypingKind::DeleteWord => last_word.clone(),
        },

        AccessibilityEvent::TextNav {
            context, boundary, ..
        } => Some(match boundary {
            Some(Boundary::Top) => format!("Top, {context}"),
            Some(Boundary::Bottom) => format!("Bottom, {context}"),
            _ => context.clone(),
        }),

        AccessibilityEvent::Selection { delta, kind, .. } => match kind {
            SelectionKind::Selected | SelectionKind::All => Some(format!("{delta} selected")),
            SelectionKind::Unselected => Some(format!("{delta} unselected")),
            SelectionKind::Cleared => Some("Selection removed".to_string()),
        },

        AccessibilityEvent::ItemNav {
            item,
            position,
            boundary,
            ..
        } => {
            let base = match position {
                Some((index, total)) => format!("{} {} of {}", item, index + 1, total),
                None => item.clone(),
            };
            Some(match boundary {
                Some(Boundary::Top) => format!("top, {base}"),
                Some(Boundary::Bottom) => format!("bottom, {base}"),
                _ => base,
            })
        }

        // The tab name alone; position and role ride the focus
        // announcement, not the change echo.
        AccessibilityEvent::TabChange { tab_name, .. } => Some(tab_name.clone()),

        // Unit directly appended: "50%".
        AccessibilityEvent::SliderChange { value, unit, .. } => Some(format!("{value}{unit}")),

        AccessibilityEvent::Filter {
            first_result,
            result_count,
            ..
        } => match first_result {
            Some(first) => Some(format!("{first} 1 of {result_count}")),
            None => Some("no results".to_string()),
        },

        AccessibilityEvent::Clipboard { op, .. } => Some(
            match op {
                ClipboardOp::Copy => "Copy",
                ClipboardOp::Cut => "Cut",
                ClipboardOp::Paste => "Paste",
            }
            .to_string(),
        ),

        AccessibilityEvent::Announce { text } => Some(text.clone()),
    }
}

/// Construct a focus announcement from the golden six.
///
/// Follows NVDA ordering: Name Role Value States Description Shortcut
/// No commas between fields. Shortcuts spoken as "Alt S" not "Alt+S".
pub fn announce_focus(label: &WidgetLabel) -> String {
    let role_str = label.role.to_string();

    let mut result = String::with_capacity(64);

    // Name (optional — nameless widgets announce as "role value ...")
    if let Some(name) = label.name_str() {
        if !name.is_empty() {
            result.push_str(name);
        }
    }

    // Role (empty for Custom — such widgets announce without a role word)
    if !role_str.is_empty() {
        if !result.is_empty() {
            result.push(' ');
        }
        result.push_str(&role_str);
    }

    // Value
    if !label.value.is_empty() {
        result.push(' ');
        result.push_str(&label.value);
    }

    // States (excluding FOCUSED — focus is implicit)
    // Dynamic state text first (e.g. "filter ed", "no filter")
    if !label.state_text.is_empty() {
        result.push(' ');
        result.push_str(&label.state_text);
    }
    let state_strings = states_to_strings(label.states);
    for s in &state_strings {
        result.push(' ');
        result.push_str(s);
    }

    // Description
    if !label.description.is_empty() {
        result.push(' ');
        result.push_str(&label.description);
    }

    // Shortcut — the first one attached to the widget, in spoken form
    // ("alt w", "control shift s").
    if let Some(shortcut) = label.shortcuts.first() {
        result.push(' ');
        result.push_str(&shortcut.combo.display_name());
    }

    result
}

/// Convert states bitflags to spoken strings (excluding FOCUSED).
fn states_to_strings(states: States) -> Vec<&'static str> {
    let mut result = Vec::new();
    if states.contains(States::DISABLED) {
        result.push("unavailable");
    }
    if states.contains(States::REQUIRED) {
        result.push("required");
    }
    if states.contains(States::WARNING) {
        result.push("warning");
    }
    result
}

/// Speak a character with punctuation expansion and case handling.
///
/// - "a" → "a"
/// - "A" → "cap A"
/// - " " → "space"
/// - "." → "dot"
/// - "\n" → "new line"
pub fn speak_char(ch: &str) -> String {
    if ch.len() == 1 {
        let c = ch.chars().next().unwrap();
        return speak_single_char(c);
    }
    // Multi-byte grapheme — return as-is
    ch.to_string()
}

fn speak_single_char(c: char) -> String {
    match c {
        ' ' => "space".to_string(),
        '\n' => "new line".to_string(),
        '\t' => "tab".to_string(),
        '\r' => "return".to_string(),

        // Punctuation
        '.' => "dot".to_string(),
        ',' => "comma".to_string(),
        ';' => "semicolon".to_string(),
        ':' => "colon".to_string(),
        '!' => "bang".to_string(),
        '?' => "question mark".to_string(),
        '\'' => "tick".to_string(),
        '"' => "quote".to_string(),
        '(' => "left paren".to_string(),
        ')' => "right paren".to_string(),
        '[' => "left bracket".to_string(),
        ']' => "right bracket".to_string(),
        '{' => "left brace".to_string(),
        '}' => "right brace".to_string(),
        '<' => "less than".to_string(),
        '>' => "greater than".to_string(),
        '/' => "slash".to_string(),
        '\\' => "backslash".to_string(),
        '|' => "pipe".to_string(),
        '@' => "at".to_string(),
        '#' => "number".to_string(),
        '$' => "dollar".to_string(),
        '%' => "percent".to_string(),
        '^' => "caret".to_string(),
        '&' => "and".to_string(),
        '*' => "star".to_string(),
        '-' => "dash".to_string(),
        '_' => "underscore".to_string(),
        '+' => "plus".to_string(),
        '=' => "equals".to_string(),
        '~' => "tilde".to_string(),
        '`' => "backtick".to_string(),

        // Uppercase letter
        c if c.is_ascii_uppercase() => format!("cap {c}"),

        // Everything else (lowercase letters, digits, etc.)
        c => c.to_string(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::types::Role;

    #[test]
    fn announce_button_with_shortcut() {
        use crate::key_combo::{Key, KeyCombo};
        use crate::types::{ShortcutAction, WidgetShortcut};

        let mut label = WidgetLabel::new("Save", Role::Button);
        label.shortcuts.push(WidgetShortcut {
            combo: KeyCombo::alt(Key::Char('s')),
            action: ShortcutAction::Jump,
        });
        assert_eq!(announce_focus(&label), "Save button alt s");

        // Only the first shortcut is announced.
        label.shortcuts.push(WidgetShortcut {
            combo: KeyCombo::ctrl(Key::Char('s')),
            action: ShortcutAction::Activate,
        });
        assert_eq!(announce_focus(&label), "Save button alt s");
    }

    #[test]
    fn announce_checkbox_states() {
        let mut label = WidgetLabel::new("Word Wrap", Role::CheckBox);
        label.value = "not checked".into();
        assert_eq!(announce_focus(&label), "Word Wrap check box not checked");

        label.value = "checked".into();
        assert_eq!(announce_focus(&label), "Word Wrap check box checked");
    }

    #[test]
    fn announce_editbox_blank() {
        let mut label = WidgetLabel::new("Notes", Role::edit());
        label.value = "blank".into();
        assert_eq!(announce_focus(&label), "Notes edit blank");
    }

    #[test]
    fn announce_listbox_with_position() {
        let mut label = WidgetLabel::new("Files", Role::ListBox);
        label.value = "readme.txt".into();
        label.state_text = "1 of 3".into();
        assert_eq!(announce_focus(&label), "Files list readme.txt 1 of 3");
    }

    #[test]
    fn announce_custom_widget_skips_role() {
        let mut label = WidgetLabel::new("Arena", Role::Custom);
        assert_eq!(announce_focus(&label), "Arena");

        label.description = "arrow keys move".into();
        assert_eq!(announce_focus(&label), "Arena arrow keys move");
    }

    #[test]
    fn announce_nameless_widget() {
        let mut label = WidgetLabel::nameless(Role::edit());
        label.value = "blank".into();
        assert_eq!(announce_focus(&label), "edit blank");
    }

    #[test]
    fn announce_disabled_required() {
        let mut label = WidgetLabel::new("Name", Role::edit());
        label.states = States::DISABLED | States::REQUIRED;
        assert_eq!(announce_focus(&label), "Name edit unavailable required");
    }

    #[test]
    fn announce_with_description() {
        let mut label = WidgetLabel::new("Volume", Role::Slider);
        label.value = "50".into();
        label.description = "master output".into();
        assert_eq!(announce_focus(&label), "Volume slider 50 master output");
    }

    #[test]
    fn speak_char_basics() {
        assert_eq!(speak_char("a"), "a");
        assert_eq!(speak_char("A"), "cap A");
        assert_eq!(speak_char(" "), "space");
        assert_eq!(speak_char("."), "dot");
        assert_eq!(speak_char("\n"), "new line");
        assert_eq!(speak_char("é"), "é");
    }

    #[test]
    fn render_focused_event() {
        let mut tree = crate::tree::Tree::new();
        let id = tree.insert(None, 0, WidgetLabel::new("Save", Role::Button));
        let ev = AccessibilityEvent::Focused {
            node: id,
            label: WidgetLabel::new("Save", Role::Button),
            context_labels: Vec::new(),
        };
        assert_eq!(render_event(&ev).as_deref(), Some("Save button"));
    }

    #[test]
    fn render_focused_with_context() {
        let mut tree = crate::tree::Tree::new();
        let id = tree.insert(None, 0, WidgetLabel::new("OK", Role::Button));
        let ev = AccessibilityEvent::Focused {
            node: id,
            label: WidgetLabel::new("OK", Role::Button),
            context_labels: vec!["Confirm delete?".into()],
        };
        assert_eq!(render_event(&ev).as_deref(), Some("Confirm delete? OK button"));
    }

    #[test]
    fn render_announce() {
        let ev = AccessibilityEvent::Announce {
            text: "Nothing to delete".into(),
        };
        assert_eq!(render_event(&ev).as_deref(), Some("Nothing to delete"));
    }

    #[test]
    fn render_clipboard() {
        let mut tree = crate::tree::Tree::new();
        let id = tree.insert(None, 0, WidgetLabel::new("N", Role::edit()));
        let ev = AccessibilityEvent::Clipboard {
            node: id,
            op: ClipboardOp::Copy,
        };
        assert_eq!(render_event(&ev).as_deref(), Some("Copy"));
    }

    #[test]
    fn render_slider_with_unit() {
        let mut tree = crate::tree::Tree::new();
        let id = tree.insert(None, 0, WidgetLabel::new("Volume", Role::Slider));
        let ev = AccessibilityEvent::SliderChange {
            node: id,
            value: 50,
            unit: "%".into(),
        };
        assert_eq!(render_event(&ev).as_deref(), Some("50%"));
    }

    #[test]
    fn render_tab_change_speaks_name_only() {
        let mut tree = crate::tree::Tree::new();
        let id = tree.insert(None, 0, WidgetLabel::new("Views", Role::TabControl));
        let ev = AccessibilityEvent::TabChange {
            node: id,
            tab_name: "Playlist".into(),
            position: (1, 3),
        };
        assert_eq!(render_event(&ev).as_deref(), Some("Playlist"));
    }
}
