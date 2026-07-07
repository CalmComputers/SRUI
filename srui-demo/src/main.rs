//! SRUI end-to-end demo: SDL window in, Prism speech out.
//!
//! Tab / Shift+Tab move focus; Alt+arrows walk the hierarchy; arrows and
//! typeahead work in the list; Space toggles the checkbox. Greet is the
//! primary widget: Enter anywhere presses it, and it reads the live list
//! and checkbox state. Escape (or Enter on Quit, or closing the window)
//! exits.

use std::time::Instant;

use srui_core::events::{OutputEvent, WidgetEvent};
use srui_core::speech;
use srui_core::ui::Ui;
use srui_core::widget::{CheckBox, ListBox};
use srui_prism::Speech;
use srui_sdl::{HostEvent, SdlHost};

fn main() -> Result<(), String> {
    let mut host = SdlHost::new("SRUI Demo", 400, 300)?;
    let mut voice = Speech::new()?;
    println!("speech backend: {}", voice.backend_name());

    let mut ui = Ui::new();
    ui.text_label(None, "SRUI demo");
    let greet = ui.button(None, "Greet");
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
    let quit = ui.button(None, "Quit");
    // Enter anywhere presses Greet; Escape anywhere presses Quit.
    ui.set_primary(greet);
    ui.set_cancel(quit);
    ui.ensure_focus();

    let start = Instant::now();
    let mut running = true;
    while running {
        for event in host.pump(5) {
            match event {
                HostEvent::Quit => running = false,
                HostEvent::KeyDown => voice.stop(),
                HostEvent::AltTap => {}
                HostEvent::Input(input) => {
                    ui.set_now(start.elapsed().as_millis() as u64);
                    ui.handle_input(&input);
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
                    OutputEvent::Widget(WidgetEvent::Activated { node }) if node == greet => {
                        let fruit = ui
                            .widget::<ListBox>(fruits)
                            .and_then(|l| l.selected_item())
                            .unwrap_or("nothing")
                            .to_string();
                        let wrapped =
                            ui.widget::<CheckBox>(wrap).map(|c| c.checked).unwrap_or(false);
                        ui.announce(format!(
                            "Hello. The fruit is {fruit}, and word wrap is {}.",
                            if wrapped { "on" } else { "off" }
                        ));
                    }
                    OutputEvent::Widget(_) => {}
                }
            }
        }
    }

    Ok(())
}
