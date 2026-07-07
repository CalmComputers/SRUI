//! Smoke test: speak one sentence through the best available backend.
//! Run with: cargo run -p srui-prism --example say

use std::thread;
use std::time::Duration;

fn main() -> Result<(), String> {
    let mut speech = srui_prism::Speech::new()?;
    println!("backend: {}", speech.backend_name());
    speech.speak("Hello from SRUI and Prism.", true)?;
    // Give an asynchronous backend time to get the audio out before the
    // process (and the backend with it) goes away.
    for _ in 0..40 {
        thread::sleep(Duration::from_millis(100));
        if !speech.is_speaking() {
            break;
        }
    }
    Ok(())
}
