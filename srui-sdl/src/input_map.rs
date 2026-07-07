//! Physical → Logical input mapping.
//!
//! Maps SDL3 keyboard events + modifiers to LogicalInput. Combos with no
//! semantic meaning surface as `RawKey` for the host's own bindings —
//! there are no application shortcuts at this layer.

use sdl3::event::{Event, WindowEvent};
use sdl3::keyboard::{Keycode, Mod};
use srui_core::input::LogicalInput;
use srui_core::key_combo::{Key, KeyCombo};

/// Maps SDL3 events to LogicalInput.
///
/// Stateful: tracks whether Ctrl/Alt are held so TextInput events can be
/// suppressed when modifiers are active (SDL3 sends TextInput for Ctrl+Space
/// on some platforms, which would otherwise produce a phantom TypeChar(' ')).
/// Also detects clean Alt taps (press and release with nothing in between),
/// which hosts commonly bind to a menu or palette.
pub struct InputMapper {
    modifiers_held: bool,
    /// True while Alt is physically held and no other key has been pressed.
    alt_clean: bool,
    /// Alt tap detected — deferred until after remaining events are drained,
    /// so a FocusLost in the same batch can cancel it.
    alt_tap_pending: bool,
}

impl InputMapper {
    pub fn new() -> Self {
        Self {
            modifiers_held: false,
            alt_clean: false,
            alt_tap_pending: false,
        }
    }

    fn is_modifier_key(keycode: Keycode) -> bool {
        matches!(
            keycode,
            Keycode::LCtrl
                | Keycode::RCtrl
                | Keycode::LAlt
                | Keycode::RAlt
                | Keycode::LShift
                | Keycode::RShift
        )
    }

    /// Map an SDL3 event to a LogicalInput, if applicable.
    pub fn map(&mut self, event: &Event) -> Option<LogicalInput> {
        match event {
            Event::KeyDown {
                keycode: Some(keycode),
                keymod,
                ..
            } => {
                let ctrl = keymod.intersects(Mod::LCTRLMOD | Mod::RCTRLMOD);
                let alt = keymod.intersects(Mod::LALTMOD | Mod::RALTMOD);
                self.modifiers_held = ctrl || alt;

                // Track clean Alt tap: Alt down with nothing else → clean.
                // Any non-modifier key while Alt is held → dirty.
                if matches!(keycode, Keycode::LAlt | Keycode::RAlt) && !ctrl {
                    self.alt_clean = true;
                } else if !Self::is_modifier_key(*keycode) {
                    self.alt_clean = false;
                }

                self.map_keydown(*keycode, *keymod)
            }

            Event::KeyUp {
                keycode: Some(keycode),
                keymod,
                ..
            } => {
                let ctrl = keymod.intersects(Mod::LCTRLMOD | Mod::RCTRLMOD);
                let alt = keymod.intersects(Mod::LALTMOD | Mod::RALTMOD);
                self.modifiers_held = ctrl || alt;

                // Alt released and nothing else was pressed → defer the tap.
                // We don't emit immediately because FocusLost (from Alt+Tab)
                // may arrive later in the same event batch.
                if matches!(keycode, Keycode::LAlt | Keycode::RAlt)
                    && !alt // no Alt key still held
                    && self.alt_clean
                {
                    self.alt_clean = false;
                    self.alt_tap_pending = true;
                }

                None
            }

            Event::Window {
                win_event: WindowEvent::FocusLost,
                ..
            } => {
                self.alt_clean = false;
                self.alt_tap_pending = false;
                None
            }

            Event::Window {
                win_event: WindowEvent::FocusGained,
                ..
            } => {
                self.alt_clean = false;
                self.alt_tap_pending = false;
                Some(LogicalInput::SpeakFocus)
            }

            Event::TextInput { text, .. } => {
                if self.modifiers_held {
                    return None;
                }
                let chars: Vec<char> = text.chars().collect();
                if chars.len() == 1 {
                    let ch = chars[0];
                    if !ch.is_control() {
                        return Some(LogicalInput::TypeChar(ch));
                    }
                }
                None
            }

            _ => None,
        }
    }

    /// Consume the deferred Alt tap, if any. Call after draining all events
    /// in the current pump cycle so FocusLost has a chance to cancel it.
    pub fn take_alt_tap(&mut self) -> bool {
        std::mem::take(&mut self.alt_tap_pending)
    }

    fn map_keydown(&self, keycode: Keycode, keymod: Mod) -> Option<LogicalInput> {
        let alt = keymod.intersects(Mod::LALTMOD | Mod::RALTMOD);
        let shift = keymod.intersects(Mod::LSHIFTMOD | Mod::RSHIFTMOD);
        let ctrl = keymod.intersects(Mod::LCTRLMOD | Mod::RCTRLMOD);

        // Alt+arrow → tree navigation; Alt+letter → widget mnemonic
        if alt && !ctrl && !shift {
            match keycode {
                Keycode::Up => return Some(LogicalInput::TreeUp),
                Keycode::Down => return Some(LogicalInput::TreeDown),
                Keycode::Left => return Some(LogicalInput::TreeLeft),
                Keycode::Right => return Some(LogicalInput::TreeRight),
                _ => {}
            }

            if let Some(ch) = keycode_to_letter(keycode) {
                return Some(LogicalInput::Shortcut(ch));
            }
        }

        // Ctrl+key combos
        if ctrl && !alt {
            match keycode {
                Keycode::C if !shift => return Some(LogicalInput::Copy),
                Keycode::X if !shift => return Some(LogicalInput::Cut),
                Keycode::V if !shift => return Some(LogicalInput::Paste),
                Keycode::A if !shift => return Some(LogicalInput::SelectAll),

                Keycode::Left if shift => return Some(LogicalInput::SelectWordLeft),
                Keycode::Right if shift => return Some(LogicalInput::SelectWordRight),
                Keycode::Left => return Some(LogicalInput::MoveWordLeft),
                Keycode::Right => return Some(LogicalInput::MoveWordRight),

                Keycode::Home if shift => return Some(LogicalInput::SelectToDocStart),
                Keycode::End if shift => return Some(LogicalInput::SelectToDocEnd),
                Keycode::Home => return Some(LogicalInput::MoveToDocStart),
                Keycode::End => return Some(LogicalInput::MoveToDocEnd),

                Keycode::Backspace => return Some(LogicalInput::DeleteWordBackward),
                Keycode::Delete => return Some(LogicalInput::DeleteWordForward),

                _ => {}
            }
        }

        // Shift+movement → selection, Shift+Backspace/Delete → word delete
        if shift && !ctrl && !alt {
            match keycode {
                Keycode::Left => return Some(LogicalInput::SelectLeft),
                Keycode::Right => return Some(LogicalInput::SelectRight),
                Keycode::Up => return Some(LogicalInput::SelectLineUp),
                Keycode::Down => return Some(LogicalInput::SelectLineDown),
                Keycode::Home => return Some(LogicalInput::SelectToLineStart),
                Keycode::End => return Some(LogicalInput::SelectToLineEnd),

                Keycode::Backspace => return Some(LogicalInput::DeleteWordBackward),
                Keycode::Delete => return Some(LogicalInput::DeleteWordForward),
                _ => {}
            }
        }

        // Plain keys
        if !alt && !ctrl {
            match keycode {
                Keycode::Tab if shift => return Some(LogicalInput::NavigatePrev),
                Keycode::Tab => return Some(LogicalInput::NavigateNext),

                Keycode::Escape if !shift => return Some(LogicalInput::Dismiss),
                Keycode::Return | Keycode::KpEnter if shift => {
                    return Some(LogicalInput::SecondaryActivate)
                }
                Keycode::Return | Keycode::KpEnter => return Some(LogicalInput::Activate),

                Keycode::Up if !shift => return Some(LogicalInput::MoveUp),
                Keycode::Down if !shift => return Some(LogicalInput::MoveDown),
                Keycode::Left if !shift => return Some(LogicalInput::MoveLeft),
                Keycode::Right if !shift => return Some(LogicalInput::MoveRight),

                Keycode::Home if !shift => return Some(LogicalInput::MoveToLineStart),
                Keycode::End if !shift => return Some(LogicalInput::MoveToLineEnd),

                Keycode::Backspace => return Some(LogicalInput::DeleteBackward),
                Keycode::Delete => return Some(LogicalInput::DeleteForward),

                _ => {}
            }
        }

        // No semantic mapping — emit RawKey for the host's shortcut matching.
        // Skip keys that will also arrive as TextInput → TypeChar, to avoid
        // double-firing shortcuts. This applies to unmodified printable keys
        // (letters, digits, space, punctuation).
        if !ctrl && !alt {
            if let Some(Key::Char(_) | Key::Space) = keycode_to_key(keycode) {
                return None;
            }
        }
        keycode_to_key(keycode).map(|key| LogicalInput::RawKey(KeyCombo::new(key, ctrl, alt, shift)))
    }
}

impl Default for InputMapper {
    fn default() -> Self {
        Self::new()
    }
}

fn keycode_to_letter(keycode: Keycode) -> Option<char> {
    match keycode {
        Keycode::A => Some('a'),
        Keycode::B => Some('b'),
        Keycode::C => Some('c'),
        Keycode::D => Some('d'),
        Keycode::E => Some('e'),
        Keycode::F => Some('f'),
        Keycode::G => Some('g'),
        Keycode::H => Some('h'),
        Keycode::I => Some('i'),
        Keycode::J => Some('j'),
        Keycode::K => Some('k'),
        Keycode::L => Some('l'),
        Keycode::M => Some('m'),
        Keycode::N => Some('n'),
        Keycode::O => Some('o'),
        Keycode::P => Some('p'),
        Keycode::Q => Some('q'),
        Keycode::R => Some('r'),
        Keycode::S => Some('s'),
        Keycode::T => Some('t'),
        Keycode::U => Some('u'),
        Keycode::V => Some('v'),
        Keycode::W => Some('w'),
        Keycode::X => Some('x'),
        Keycode::Y => Some('y'),
        Keycode::Z => Some('z'),
        _ => None,
    }
}

fn keycode_to_key(keycode: Keycode) -> Option<Key> {
    match keycode {
        Keycode::A => Some(Key::Char('a')),
        Keycode::B => Some(Key::Char('b')),
        Keycode::C => Some(Key::Char('c')),
        Keycode::D => Some(Key::Char('d')),
        Keycode::E => Some(Key::Char('e')),
        Keycode::F => Some(Key::Char('f')),
        Keycode::G => Some(Key::Char('g')),
        Keycode::H => Some(Key::Char('h')),
        Keycode::I => Some(Key::Char('i')),
        Keycode::J => Some(Key::Char('j')),
        Keycode::K => Some(Key::Char('k')),
        Keycode::L => Some(Key::Char('l')),
        Keycode::M => Some(Key::Char('m')),
        Keycode::N => Some(Key::Char('n')),
        Keycode::O => Some(Key::Char('o')),
        Keycode::P => Some(Key::Char('p')),
        Keycode::Q => Some(Key::Char('q')),
        Keycode::R => Some(Key::Char('r')),
        Keycode::S => Some(Key::Char('s')),
        Keycode::T => Some(Key::Char('t')),
        Keycode::U => Some(Key::Char('u')),
        Keycode::V => Some(Key::Char('v')),
        Keycode::W => Some(Key::Char('w')),
        Keycode::X => Some(Key::Char('x')),
        Keycode::Y => Some(Key::Char('y')),
        Keycode::Z => Some(Key::Char('z')),
        Keycode::_0 => Some(Key::Char('0')),
        Keycode::_1 => Some(Key::Char('1')),
        Keycode::_2 => Some(Key::Char('2')),
        Keycode::_3 => Some(Key::Char('3')),
        Keycode::_4 => Some(Key::Char('4')),
        Keycode::_5 => Some(Key::Char('5')),
        Keycode::_6 => Some(Key::Char('6')),
        Keycode::_7 => Some(Key::Char('7')),
        Keycode::_8 => Some(Key::Char('8')),
        Keycode::_9 => Some(Key::Char('9')),
        Keycode::F1 => Some(Key::F(1)),
        Keycode::F2 => Some(Key::F(2)),
        Keycode::F3 => Some(Key::F(3)),
        Keycode::F4 => Some(Key::F(4)),
        Keycode::F5 => Some(Key::F(5)),
        Keycode::F6 => Some(Key::F(6)),
        Keycode::F7 => Some(Key::F(7)),
        Keycode::F8 => Some(Key::F(8)),
        Keycode::F9 => Some(Key::F(9)),
        Keycode::F10 => Some(Key::F(10)),
        Keycode::F11 => Some(Key::F(11)),
        Keycode::F12 => Some(Key::F(12)),
        Keycode::Up => Some(Key::Up),
        Keycode::Down => Some(Key::Down),
        Keycode::Left => Some(Key::Left),
        Keycode::Right => Some(Key::Right),
        Keycode::Home => Some(Key::Home),
        Keycode::End => Some(Key::End),
        Keycode::PageUp => Some(Key::PageUp),
        Keycode::PageDown => Some(Key::PageDown),
        Keycode::Return | Keycode::KpEnter => Some(Key::Enter),
        Keycode::Escape => Some(Key::Escape),
        Keycode::Tab => Some(Key::Tab),
        Keycode::Space => Some(Key::Space),
        Keycode::Backspace => Some(Key::Backspace),
        Keycode::Delete => Some(Key::Delete),
        // Punctuation
        Keycode::LeftBracket => Some(Key::Char('[')),
        Keycode::RightBracket => Some(Key::Char(']')),
        Keycode::Semicolon => Some(Key::Char(';')),
        Keycode::Apostrophe => Some(Key::Char('\'')),
        Keycode::Comma => Some(Key::Char(',')),
        Keycode::Period => Some(Key::Char('.')),
        Keycode::Slash => Some(Key::Char('/')),
        Keycode::Backslash => Some(Key::Char('\\')),
        Keycode::Grave => Some(Key::Char('`')),
        Keycode::Minus => Some(Key::Char('-')),
        Keycode::Equals => Some(Key::Char('=')),

        // Media keys
        Keycode::MediaPlayPause | Keycode::MediaPlay | Keycode::MediaPause => {
            Some(Key::MediaPlayPause)
        }
        Keycode::MediaNextTrack => Some(Key::MediaNextTrack),
        Keycode::MediaPreviousTrack => Some(Key::MediaPreviousTrack),
        Keycode::MediaStop => Some(Key::MediaStop),
        _ => None,
    }
}
