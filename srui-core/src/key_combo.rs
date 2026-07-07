//! Key combo representation — a physical key plus modifier state.

use crate::input::LogicalInput;

/// A physical key, independent of modifier state.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum Key {
    Char(char), // lowercase-normalized
    F(u8),      // F1..F12
    Enter,
    Escape,
    Tab,
    Space,
    Up,
    Down,
    Left,
    Right,
    Home,
    End,
    Delete,
    Backspace,
    PageUp,
    PageDown,
    MediaPlayPause,
    MediaNextTrack,
    MediaPreviousTrack,
    MediaStop,
}

/// A key combined with modifier state.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct KeyCombo {
    pub key: Key,
    pub ctrl: bool,
    pub alt: bool,
    pub shift: bool,
}

impl KeyCombo {
    pub const fn new(key: Key, ctrl: bool, alt: bool, shift: bool) -> Self {
        Self {
            key,
            ctrl,
            alt,
            shift,
        }
    }

    /// Plain key with no modifiers.
    pub const fn plain(key: Key) -> Self {
        Self::new(key, false, false, false)
    }

    /// Ctrl+key.
    pub const fn ctrl(key: Key) -> Self {
        Self::new(key, true, false, false)
    }

    /// Alt+key.
    pub const fn alt(key: Key) -> Self {
        Self::new(key, false, true, false)
    }

    /// Shift+key.
    pub const fn shift(key: Key) -> Self {
        Self::new(key, false, false, true)
    }

    /// Ctrl+Shift+key.
    pub const fn ctrl_shift(key: Key) -> Self {
        Self::new(key, true, false, true)
    }

    /// Alt+Shift+key.
    pub const fn alt_shift(key: Key) -> Self {
        Self::new(key, false, true, true)
    }

    /// Ctrl+Alt+Shift+key.
    pub const fn ctrl_alt_shift(key: Key) -> Self {
        Self::new(key, true, true, true)
    }

    /// Reverse-map a LogicalInput to the KeyCombo that would produce it
    /// under the default input map. Returns None for synthetic inputs
    /// (SpeakFocus) that don't correspond to a single physical key combo.
    pub fn from_logical(input: &LogicalInput) -> Option<Self> {
        let combo = match input {
            // Navigation
            LogicalInput::NavigateNext => Self::plain(Key::Tab),
            LogicalInput::NavigatePrev => Self::new(Key::Tab, false, false, true),
            LogicalInput::TreeUp => Self::new(Key::Up, false, true, false),
            LogicalInput::TreeDown => Self::new(Key::Down, false, true, false),
            LogicalInput::TreeLeft => Self::new(Key::Left, false, true, false),
            LogicalInput::TreeRight => Self::new(Key::Right, false, true, false),
            LogicalInput::Shortcut(ch) => {
                Self::new(Key::Char(ch.to_ascii_lowercase()), false, true, false)
            }

            // Widget input
            LogicalInput::Activate => Self::plain(Key::Enter),
            LogicalInput::SecondaryActivate => Self::shift(Key::Enter),
            LogicalInput::MoveUp | LogicalInput::MoveLineUp => Self::plain(Key::Up),
            LogicalInput::MoveDown | LogicalInput::MoveLineDown => Self::plain(Key::Down),
            LogicalInput::MoveLeft => Self::plain(Key::Left),
            LogicalInput::MoveRight => Self::plain(Key::Right),
            LogicalInput::MoveWordLeft => Self::ctrl(Key::Left),
            LogicalInput::MoveWordRight => Self::ctrl(Key::Right),
            LogicalInput::MoveToLineStart => Self::plain(Key::Home),
            LogicalInput::MoveToLineEnd => Self::plain(Key::End),
            LogicalInput::MoveToDocStart => Self::ctrl(Key::Home),
            LogicalInput::MoveToDocEnd => Self::ctrl(Key::End),

            // Selection
            LogicalInput::SelectLeft => Self::new(Key::Left, false, false, true),
            LogicalInput::SelectRight => Self::new(Key::Right, false, false, true),
            LogicalInput::SelectWordLeft => Self::new(Key::Left, true, false, true),
            LogicalInput::SelectWordRight => Self::new(Key::Right, true, false, true),
            LogicalInput::SelectToLineStart => Self::new(Key::Home, false, false, true),
            LogicalInput::SelectToLineEnd => Self::new(Key::End, false, false, true),
            LogicalInput::SelectToDocStart => Self::new(Key::Home, true, false, true),
            LogicalInput::SelectToDocEnd => Self::new(Key::End, true, false, true),
            LogicalInput::SelectLineUp => Self::new(Key::Up, false, false, true),
            LogicalInput::SelectLineDown => Self::new(Key::Down, false, false, true),
            LogicalInput::SelectAll => Self::ctrl(Key::Char('a')),

            // Text editing
            LogicalInput::TypeChar(' ') => Self::plain(Key::Space),
            LogicalInput::TypeChar(ch) => Self::plain(Key::Char(ch.to_ascii_lowercase())),
            LogicalInput::DeleteBackward => Self::plain(Key::Backspace),
            LogicalInput::DeleteForward => Self::plain(Key::Delete),
            LogicalInput::DeleteWordBackward => Self::ctrl(Key::Backspace),
            LogicalInput::DeleteWordForward => Self::ctrl(Key::Delete),

            // Clipboard
            LogicalInput::Copy => Self::ctrl(Key::Char('c')),
            LogicalInput::Cut => Self::ctrl(Key::Char('x')),
            LogicalInput::Paste => Self::ctrl(Key::Char('v')),

            // Modal dismiss
            LogicalInput::Dismiss => Self::plain(Key::Escape),

            // RawKey already carries a KeyCombo
            LogicalInput::RawKey(combo) => *combo,

            // Synthetic — no physical key
            LogicalInput::SpeakFocus => return None,
        };
        Some(combo)
    }

    /// Check if this combo matches the given logical input.
    pub fn matches_input(&self, input: &LogicalInput) -> bool {
        Self::from_logical(input) == Some(*self)
    }

    /// Human-readable display for speech: "control alt shift s".
    /// Modifier order: control, alt, shift, then the key. Space-separated, lowercase.
    pub fn display_name(&self) -> String {
        let mut parts = Vec::new();
        if self.ctrl {
            parts.push("control".to_string());
        }
        if self.alt {
            parts.push("alt".to_string());
        }
        if self.shift {
            parts.push("shift".to_string());
        }
        let key_name = match self.key {
            Key::Char(ch) => ch.to_ascii_lowercase().to_string(),
            Key::F(n) => format!("f{n}"),
            Key::Enter => "enter".to_string(),
            Key::Escape => "escape".to_string(),
            Key::Tab => "tab".to_string(),
            Key::Space => "space".to_string(),
            Key::Up => "up".to_string(),
            Key::Down => "down".to_string(),
            Key::Left => "left".to_string(),
            Key::Right => "right".to_string(),
            Key::Home => "home".to_string(),
            Key::End => "end".to_string(),
            Key::Delete => "delete".to_string(),
            Key::Backspace => "backspace".to_string(),
            Key::PageUp => "page up".to_string(),
            Key::PageDown => "page down".to_string(),
            Key::MediaPlayPause => "play pause".to_string(),
            Key::MediaNextTrack => "next track".to_string(),
            Key::MediaPreviousTrack => "previous track".to_string(),
            Key::MediaStop => "stop".to_string(),
        };
        parts.push(key_name);
        parts.join(" ")
    }
}

impl std::fmt::Display for KeyCombo {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}", self.display_name())
    }
}

// ── Config string form ───────────────────────────────────────────────
//
// KeyCombo has a compact, human-readable string form: "ctrl+shift+s",
// "alt+f2", "enter". Modifier order is always ctrl, alt, shift. Hosts
// that persist bindings serialize this string; the core owns no
// persistence format.

impl KeyCombo {
    /// Compact string form: "ctrl+shift+s", "alt+f2", "enter".
    /// Modifier order: ctrl, alt, shift, then key. Plus-separated, lowercase.
    pub fn to_string_config(&self) -> String {
        let mut parts = Vec::new();
        if self.ctrl {
            parts.push("ctrl".to_string());
        }
        if self.alt {
            parts.push("alt".to_string());
        }
        if self.shift {
            parts.push("shift".to_string());
        }
        parts.push(key_to_str(self.key).to_string());
        parts.join("+")
    }

    /// Parse a config string like "ctrl+shift+s" back into a KeyCombo.
    pub fn parse_config(s: &str) -> Result<Self, String> {
        let mut ctrl = false;
        let mut alt = false;
        let mut shift = false;
        let mut key_part = None;

        for part in s.split('+') {
            let part = part.trim().to_ascii_lowercase();
            match part.as_str() {
                "ctrl" | "control" => ctrl = true,
                "alt" => alt = true,
                "shift" => shift = true,
                _ => {
                    if key_part.is_some() {
                        return Err(format!("multiple key parts in shortcut: {s:?}"));
                    }
                    key_part = Some(part);
                }
            }
        }

        let key_str = key_part.ok_or_else(|| format!("no key in shortcut: {s:?}"))?;
        let key = key_from_str(&key_str)
            .ok_or_else(|| format!("unknown key {key_str:?} in shortcut: {s:?}"))?;

        Ok(Self { key, ctrl, alt, shift })
    }
}

/// Key → canonical config name.
fn key_to_str(key: Key) -> &'static str {
    match key {
        Key::Char('a') => "a", Key::Char('b') => "b", Key::Char('c') => "c",
        Key::Char('d') => "d", Key::Char('e') => "e", Key::Char('f') => "f",
        Key::Char('g') => "g", Key::Char('h') => "h", Key::Char('i') => "i",
        Key::Char('j') => "j", Key::Char('k') => "k", Key::Char('l') => "l",
        Key::Char('m') => "m", Key::Char('n') => "n", Key::Char('o') => "o",
        Key::Char('p') => "p", Key::Char('q') => "q", Key::Char('r') => "r",
        Key::Char('s') => "s", Key::Char('t') => "t", Key::Char('u') => "u",
        Key::Char('v') => "v", Key::Char('w') => "w", Key::Char('x') => "x",
        Key::Char('y') => "y", Key::Char('z') => "z",
        Key::Char('0') => "0", Key::Char('1') => "1", Key::Char('2') => "2",
        Key::Char('3') => "3", Key::Char('4') => "4", Key::Char('5') => "5",
        Key::Char('6') => "6", Key::Char('7') => "7", Key::Char('8') => "8",
        Key::Char('9') => "9",
        Key::Char(_) => "?",
        Key::F(1) => "f1", Key::F(2) => "f2", Key::F(3) => "f3",
        Key::F(4) => "f4", Key::F(5) => "f5", Key::F(6) => "f6",
        Key::F(7) => "f7", Key::F(8) => "f8", Key::F(9) => "f9",
        Key::F(10) => "f10", Key::F(11) => "f11", Key::F(12) => "f12",
        Key::F(_) => "f?",
        Key::Enter => "enter",
        Key::Escape => "escape",
        Key::Tab => "tab",
        Key::Space => "space",
        Key::Up => "up",
        Key::Down => "down",
        Key::Left => "left",
        Key::Right => "right",
        Key::Home => "home",
        Key::End => "end",
        Key::Delete => "delete",
        Key::Backspace => "backspace",
        Key::PageUp => "pageup",
        Key::PageDown => "pagedown",
        Key::MediaPlayPause => "playpause",
        Key::MediaNextTrack => "nexttrack",
        Key::MediaPreviousTrack => "previoustrack",
        Key::MediaStop => "mediastop",
    }
}

/// Config name → Key. Accepts canonical names and common aliases.
fn key_from_str(s: &str) -> Option<Key> {
    // Single character → Char
    if s.len() == 1 {
        let ch = s.chars().next().unwrap();
        if ch.is_ascii_alphanumeric() {
            return Some(Key::Char(ch.to_ascii_lowercase()));
        }
    }
    // F-keys
    if let Some(rest) = s.strip_prefix('f') {
        if let Ok(n) = rest.parse::<u8>() {
            if (1..=12).contains(&n) {
                return Some(Key::F(n));
            }
        }
    }
    match s {
        "enter" | "return" => Some(Key::Enter),
        "escape" | "esc" => Some(Key::Escape),
        "tab" => Some(Key::Tab),
        "space" => Some(Key::Space),
        "up" => Some(Key::Up),
        "down" => Some(Key::Down),
        "left" => Some(Key::Left),
        "right" => Some(Key::Right),
        "home" => Some(Key::Home),
        "end" => Some(Key::End),
        "delete" | "del" => Some(Key::Delete),
        "backspace" => Some(Key::Backspace),
        "pageup" | "pgup" => Some(Key::PageUp),
        "pagedown" | "pgdn" | "pgdown" => Some(Key::PageDown),
        "playpause" => Some(Key::MediaPlayPause),
        "nexttrack" => Some(Key::MediaNextTrack),
        "previoustrack" | "prevtrack" => Some(Key::MediaPreviousTrack),
        "mediastop" | "stop" => Some(Key::MediaStop),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn display_plain_key() {
        assert_eq!(KeyCombo::plain(Key::Enter).display_name(), "enter");
        assert_eq!(KeyCombo::plain(Key::Space).display_name(), "space");
    }

    #[test]
    fn display_ctrl_key() {
        assert_eq!(KeyCombo::ctrl(Key::Char('s')).display_name(), "control s");
        assert_eq!(KeyCombo::ctrl(Key::Home).display_name(), "control home");
    }

    #[test]
    fn display_multi_modifier() {
        let combo = KeyCombo::new(Key::Char('p'), true, false, true);
        assert_eq!(combo.display_name(), "control shift p");
    }

    #[test]
    fn display_alt_f4() {
        let combo = KeyCombo::new(Key::F(4), false, true, false);
        assert_eq!(combo.display_name(), "alt f4");
    }

    #[test]
    fn from_logical_copy() {
        let combo = KeyCombo::from_logical(&LogicalInput::Copy).unwrap();
        assert_eq!(combo, KeyCombo::ctrl(Key::Char('c')));
    }

    #[test]
    fn from_logical_type_char() {
        let combo = KeyCombo::from_logical(&LogicalInput::TypeChar('a')).unwrap();
        assert_eq!(combo, KeyCombo::plain(Key::Char('a')));
    }

    #[test]
    fn from_logical_space() {
        let combo = KeyCombo::from_logical(&LogicalInput::TypeChar(' ')).unwrap();
        assert_eq!(combo, KeyCombo::plain(Key::Space));
    }

    #[test]
    fn from_logical_dismiss() {
        let combo = KeyCombo::from_logical(&LogicalInput::Dismiss).unwrap();
        assert_eq!(combo, KeyCombo::plain(Key::Escape));
    }

    #[test]
    fn from_logical_raw_key_passthrough() {
        let raw = KeyCombo::new(Key::Char('s'), true, false, false);
        let combo = KeyCombo::from_logical(&LogicalInput::RawKey(raw)).unwrap();
        assert_eq!(combo, raw);
    }

    #[test]
    fn from_logical_speak_focus_returns_none() {
        assert!(KeyCombo::from_logical(&LogicalInput::SpeakFocus).is_none());
    }

    #[test]
    fn matches_input_positive() {
        let ctrl_s = KeyCombo::ctrl(Key::Char('s'));
        let raw = LogicalInput::RawKey(KeyCombo::ctrl(Key::Char('s')));
        assert!(ctrl_s.matches_input(&raw));
    }

    #[test]
    fn matches_input_negative() {
        let ctrl_s = KeyCombo::ctrl(Key::Char('s'));
        assert!(!ctrl_s.matches_input(&LogicalInput::Copy));
    }

    #[test]
    fn matches_input_via_semantic() {
        let ctrl_c = KeyCombo::ctrl(Key::Char('c'));
        assert!(ctrl_c.matches_input(&LogicalInput::Copy));
    }

    #[test]
    fn shortcut_maps_to_alt_char() {
        let combo = KeyCombo::from_logical(&LogicalInput::Shortcut('s')).unwrap();
        assert_eq!(combo, KeyCombo::new(Key::Char('s'), false, true, false));
    }

    // ── Config string roundtrip tests ────────────────────────────────

    #[test]
    fn config_string_plain_key() {
        assert_eq!(KeyCombo::plain(Key::Enter).to_string_config(), "enter");
    }

    #[test]
    fn config_string_ctrl_s() {
        assert_eq!(KeyCombo::ctrl(Key::Char('s')).to_string_config(), "ctrl+s");
    }

    #[test]
    fn config_string_ctrl_shift_p() {
        let combo = KeyCombo::new(Key::Char('p'), true, false, true);
        assert_eq!(combo.to_string_config(), "ctrl+shift+p");
    }

    #[test]
    fn config_string_alt_f4() {
        let combo = KeyCombo::new(Key::F(4), false, true, false);
        assert_eq!(combo.to_string_config(), "alt+f4");
    }

    #[test]
    fn config_string_roundtrip() {
        let combos = vec![
            KeyCombo::plain(Key::Enter),
            KeyCombo::ctrl(Key::Char('s')),
            KeyCombo::alt(Key::F(12)),
            KeyCombo::ctrl_shift(Key::Char('p')),
            KeyCombo::new(Key::Delete, true, true, true),
            KeyCombo::plain(Key::PageUp),
            KeyCombo::plain(Key::MediaPlayPause),
        ];
        for combo in combos {
            let s = combo.to_string_config();
            let parsed = KeyCombo::parse_config(&s).unwrap();
            assert_eq!(combo, parsed, "roundtrip failed for {s:?}");
        }
    }

    #[test]
    fn parse_aliases() {
        assert_eq!(
            KeyCombo::parse_config("control+esc").unwrap(),
            KeyCombo::ctrl(Key::Escape),
        );
        assert_eq!(
            KeyCombo::parse_config("ctrl+del").unwrap(),
            KeyCombo::ctrl(Key::Delete),
        );
        assert_eq!(
            KeyCombo::parse_config("ctrl+pgup").unwrap(),
            KeyCombo::ctrl(Key::PageUp),
        );
    }

    #[test]
    fn parse_rejects_empty() {
        assert!(KeyCombo::parse_config("").is_err());
    }

    #[test]
    fn parse_rejects_double_key() {
        assert!(KeyCombo::parse_config("a+b").is_err());
    }
}
