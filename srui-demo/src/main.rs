//! SRUI end-to-end demo: SDL window in, Prism speech out.
//!
//! Tab / Shift+Tab move focus; Alt+arrows walk the hierarchy; arrows and
//! typeahead work in the list; Space toggles the checkbox. Greet is the
//! primary widget: Enter anywhere presses it (Ctrl+G too, as a host-side
//! binding), and it reads the live name, list, and checkbox state.
//! Escape (or Enter on Quit, or closing the window) exits.

use std::time::Instant;

use srui_core::events::{OutputEvent, WidgetEvent};
use srui_core::input::LogicalInput;
use srui_core::key_combo::{Key, KeyCombo};
use srui_core::speech;
use srui_core::tree::NodeId;
use srui_core::ui::Ui;
use srui_core::widget::{CheckBox, EditBox, ListBox};
use srui_prism::Speech;
use srui_sdl::{HostEvent, SdlHost};

/// Compose and queue the greeting from live widget state.
fn greet(ui: &mut Ui, name_field: NodeId, fruits: NodeId, wrap: NodeId) {
    let fruit = ui
        .widget::<ListBox>(fruits)
        .and_then(|l| l.selected_item())
        .unwrap_or("nothing")
        .to_string();
    let wrapped = ui.widget::<CheckBox>(wrap).map(|c| c.checked).unwrap_or(false);
    let who = ui
        .widget::<EditBox>(name_field)
        .map(|e| e.text())
        .filter(|t| !t.is_empty())
        .unwrap_or_else(|| "stranger".to_string());
    ui.announce(format!(
        "Hello, {who}. The fruit is {fruit}, and word wrap is {}.",
        if wrapped { "on" } else { "off" }
    ));
}

fn main() -> Result<(), String> {
    let mut host = SdlHost::new("SRUI Demo", 400, 300)?;
    let mut voice = Speech::new()?;
    println!("speech backend: {}", voice.backend_name());

    let mut ui = Ui::new();
    ui.set_clipboard(host.clipboard());
    ui.text_label(None, "SRUI demo");
    let name_field = ui.editbox(None, "Your name", "");
    let _notes = ui.editbox_multiline(None, "Notes", "");
    let greet_btn = ui.button(None, "Greet");
    let wrap = ui.checkbox(None, "Word Wrap", false);
    let options = ui.group(None, "Options");
    ui.checkbox(Some(options), "Autosave", true);
    ui.checkbox(Some(options), "Telemetry", false);
    let fruits = ui.listbox(
        None,
        "Fruits",
        ["apple", "banana", "cherry", "date", "elderberry"]
            .iter()
            .map(|s| s.to_string())
            .collect(),
        true,
    );
    let _volume = ui.slider_widget(
        None,
        "Volume",
        srui_core::widget::Slider::new(50, 0, 100).with_unit("%"),
    );
    let _views = ui.tab_control(
        None,
        "Views",
        vec!["Library".into(), "Playlist".into(), "Effects".into()],
        0,
    );
    let _commands = ui.filter_listbox(
        None,
        "Commands",
        [
            "Save File", "Save As", "Open File", "Open Recent", "Close Tab",
            "Find", "Find Next", "Replace", "Go To Line", "Toggle Word Wrap",
            "Zoom In", "Zoom Out",
        ]
        .iter()
        .map(|s| s.to_string())
        .collect(),
    );
    let _shortcut = ui.shortcut_field(None, "Custom shortcut");
    let quit = ui.button(None, "Quit");
    // Enter anywhere presses Greet; Escape anywhere presses Quit.
    ui.set_primary(greet_btn);
    ui.set_cancel(quit);
    ui.ensure_focus();

    let start = Instant::now();
    let mut running = true;
    while running {
        // Every iteration, not just on input: tickers fire from here.
        ui.set_now(start.elapsed().as_millis() as u64);
        for event in host.pump(5) {
            match event {
                HostEvent::Quit => running = false,
                HostEvent::KeyDown => voice.stop(),
                HostEvent::AltTap => {}
                HostEvent::Input(input) => {
                    if !ui.handle_input(&input) {
                        // Host-side bindings: unconsumed input is ours to
                        // match. Ctrl+G greets from anywhere.
                        if input == LogicalInput::RawKey(KeyCombo::ctrl(Key::Char('g'))) {
                            greet(&mut ui, name_field, fruits, wrap);
                        }
                    }
                }
            }
        }

        // Drain until quiescent: reactions to widget events (e.g. the
        // Greet announcement) queue further output that must be spoken
        // this iteration, not after the next pump.
        loop {
            let batch = ui.drain_events();
            if batch.is_empty() {
                break;
            }
            for out in batch {
                match out {
                    OutputEvent::Accessibility(event) => {
                        if let Some(text) = speech::render_event(&event) {
                            let _ = voice.speak(&text, false);
                        }
                    }
                    OutputEvent::Widget(WidgetEvent::Activated { node }) if node == quit => {
                        running = false;
                    }
                    OutputEvent::Widget(WidgetEvent::Activated { node }) if node == greet_btn => {
                        greet(&mut ui, name_field, fruits, wrap);
                    }
                    OutputEvent::Widget(_) | OutputEvent::Tick { .. } => {}
                }
            }
        }
    }

    Ok(())
}
