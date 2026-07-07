//! Reserved key combos — the combos the framework itself consumes.
//!
//! Key bindings are host policy; the core's only contribution is naming
//! the combos it will never let a binding see, each with a spoken reason
//! for bind dialogs to announce. Everything else — including combos like
//! Ctrl+Tab or Escape that merely *might* collide with a cancel widget —
//! is at most a soft conflict for the host to warn about (see
//! `Role::reserves_key` for the per-widget-role side of that).

use crate::key_combo::{Key, KeyCombo};

/// If `combo` is categorically unbindable, the spoken reason why.
pub fn reserved_reason(combo: &KeyCombo) -> Option<&'static str> {
    // Plain Tab / Shift+Tab: the focus ring must always work.
    if combo.key == Key::Tab && !combo.ctrl && !combo.alt {
        return Some("Tab is reserved for moving between widgets");
    }
    // Alt+letter: widget mnemonics.
    if combo.alt && !combo.ctrl && !combo.shift {
        if let Key::Char(c) = combo.key {
            if c.is_ascii_alphabetic() {
                return Some("Alt plus a letter is reserved for widget shortcuts");
            }
        }
        // Alt+arrows: hierarchy navigation.
        if matches!(combo.key, Key::Up | Key::Down | Key::Left | Key::Right) {
            return Some("Alt plus arrows is reserved for tree navigation");
        }
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn tab_and_shift_tab_reserved() {
        assert!(reserved_reason(&KeyCombo::plain(Key::Tab)).is_some());
        assert!(reserved_reason(&KeyCombo::shift(Key::Tab)).is_some());
    }

    #[test]
    fn ctrl_tab_is_bindable() {
        assert!(reserved_reason(&KeyCombo::ctrl(Key::Tab)).is_none());
        assert!(reserved_reason(&KeyCombo::ctrl_shift(Key::Tab)).is_none());
    }

    #[test]
    fn alt_letter_reserved_but_alt_shift_letter_free() {
        assert!(reserved_reason(&KeyCombo::alt(Key::Char('s'))).is_some());
        assert!(reserved_reason(&KeyCombo::alt_shift(Key::Char('s'))).is_none());
        assert!(reserved_reason(&KeyCombo::alt(Key::Char('5'))).is_none());
    }

    #[test]
    fn alt_arrows_reserved() {
        for key in [Key::Up, Key::Down, Key::Left, Key::Right] {
            assert!(reserved_reason(&KeyCombo::alt(key)).is_some());
            // Plain and Ctrl arrows stay free (widget navigation and
            // host bindings respectively).
            assert!(reserved_reason(&KeyCombo::plain(key)).is_none());
            assert!(reserved_reason(&KeyCombo::ctrl(key)).is_none());
        }
    }

    #[test]
    fn ordinary_commands_are_bindable() {
        assert!(reserved_reason(&KeyCombo::ctrl(Key::Char('s'))).is_none());
        assert!(reserved_reason(&KeyCombo::plain(Key::F(5))).is_none());
        assert!(reserved_reason(&KeyCombo::plain(Key::Escape)).is_none());
    }
}
