//! SDL3 host layer — a hidden-rendering window that exists to receive
//! keyboard focus, plus the physical→logical input translation.
//!
//! This crate is a host convenience, not part of the core contract: it
//! turns SDL events into `LogicalInput` and host signals, and the
//! application owns the loop.

pub mod input_map;

pub use input_map::InputMapper;

use sdl3::event::{Event, WindowEvent};
use srui_core::clipboard::Clipboard;
use srui_core::input::LogicalInput;
use srui_core::key_combo::KeyCombo;

/// The system clipboard via SDL.
pub struct SdlClipboard {
    clipboard: sdl3::clipboard::ClipboardUtil,
}

impl Clipboard for SdlClipboard {
    fn read(&mut self) -> Option<String> {
        match self.clipboard.clipboard_text() {
            Ok(text) if !text.is_empty() => Some(text),
            _ => None,
        }
    }

    fn write(&mut self, text: &str) {
        let _ = self.clipboard.set_clipboard_text(text);
    }
}

/// One thing the host loop reacts to.
#[derive(Debug, Clone, PartialEq)]
pub enum HostEvent {
    /// A logical input for `Ui::handle_input`.
    Input(LogicalInput),
    /// A physical key went down. Emitted before the corresponding
    /// `Input`, so the loop can silence speech first — this keeps NVDA's
    /// missed-key heuristic from re-speaking on our behalf.
    KeyDown,
    /// A physical key transition, for game-style input: the initial
    /// press (`pressed`, not `repeat`), auto-repeats (`pressed` and
    /// `repeat`), and the release. Runs parallel to the logical `Input`
    /// stream and independent of it — a press fires here even when the
    /// core consumes the corresponding input. Bare modifier presses have
    /// no `KeyCombo` form and do not appear.
    Key {
        combo: KeyCombo,
        pressed: bool,
        repeat: bool,
    },
    /// A clean Alt tap (press and release with nothing in between).
    /// Hosts commonly bind this to a menu or command palette.
    AltTap,
    /// The window lost keyboard focus. Game-style hosts zero their
    /// held-key state here: the matching releases will never arrive.
    FocusLost,
    /// The window manager asked us to quit.
    Quit,
}

/// The SDL window and event pump.
pub struct SdlHost {
    // Field order is drop order: pump and window before the contexts.
    event_pump: sdl3::EventPump,
    _window: sdl3::video::Window,
    _video: sdl3::VideoSubsystem,
    _sdl: sdl3::Sdl,
    mapper: InputMapper,
}

impl SdlHost {
    /// Create the window and start text input (required for TypeChar
    /// events).
    pub fn new(title: &str, width: u32, height: u32) -> Result<Self, String> {
        let sdl = sdl3::init().map_err(|e| e.to_string())?;
        let video = sdl.video().map_err(|e| e.to_string())?;
        let window = video
            .window(title, width, height)
            .position_centered()
            .build()
            .map_err(|e| e.to_string())?;
        video.text_input().start(&window);
        let event_pump = sdl.event_pump().map_err(|e| e.to_string())?;

        Ok(Self {
            event_pump,
            _window: window,
            _video: video,
            _sdl: sdl,
            mapper: InputMapper::new(),
        })
    }

    /// The system clipboard, for `Ui::set_clipboard`.
    pub fn clipboard(&self) -> Box<SdlClipboard> {
        Box::new(SdlClipboard {
            clipboard: self._video.clipboard(),
        })
    }

    /// Block up to `timeout_ms` for events, then drain everything pending.
    /// Returns the batch in order; empty when the timeout passed quietly.
    pub fn pump(&mut self, timeout_ms: u32) -> Vec<HostEvent> {
        let mut out = Vec::new();

        if let Some(event) = self.event_pump.wait_event_timeout(timeout_ms) {
            self.dispatch(&event, &mut out);
            // poll_iter borrows the pump; collect first.
            let rest: Vec<Event> = self.event_pump.poll_iter().collect();
            for event in rest {
                self.dispatch(&event, &mut out);
            }
            // Alt tap resolves only after the whole batch is seen, so a
            // FocusLost in the same batch can cancel it.
            if self.mapper.take_alt_tap() {
                out.push(HostEvent::AltTap);
            }
        }

        out
    }

    fn dispatch(&mut self, event: &Event, out: &mut Vec<HostEvent>) {
        if matches!(event, Event::Quit { .. }) {
            out.push(HostEvent::Quit);
            return;
        }
        if matches!(event, Event::KeyDown { .. }) {
            out.push(HostEvent::KeyDown);
        }
        // The physical key stream, ahead of the logical Input so a
        // game reacts before the input's side effects land.
        match event {
            Event::KeyDown {
                keycode: Some(keycode),
                keymod,
                repeat,
                ..
            } => {
                if let Some(combo) = input_map::physical_combo(*keycode, *keymod) {
                    out.push(HostEvent::Key {
                        combo,
                        pressed: true,
                        repeat: *repeat,
                    });
                }
            }
            Event::KeyUp {
                keycode: Some(keycode),
                keymod,
                ..
            } => {
                if let Some(combo) = input_map::physical_combo(*keycode, *keymod) {
                    out.push(HostEvent::Key {
                        combo,
                        pressed: false,
                        repeat: false,
                    });
                }
            }
            Event::Window {
                win_event: WindowEvent::FocusLost,
                ..
            } => out.push(HostEvent::FocusLost),
            _ => {}
        }
        if let Some(logical) = self.mapper.map(event) {
            out.push(HostEvent::Input(logical));
        }
    }
}
