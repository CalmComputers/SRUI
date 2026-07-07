//! Safe wrapper over Prism — speech and braille output through the best
//! available backend (screen reader if one is running, platform TTS
//! otherwise).
//!
//! `Speech` owns a Prism context and one backend instance. It is
//! deliberately not `Send`: create it on the thread that speaks (raw
//! pointers make this automatic; the field types enforce it).

use std::ffi::{CStr, CString};

use srui_prism_sys as sys;

/// A speech/braille output channel. Dropping it shuts Prism down.
pub struct Speech {
    ctx: *mut sys::PrismContext,
    backend: *mut sys::PrismBackend,
}

impl Speech {
    /// Initialize Prism and acquire the best available backend.
    pub fn new() -> Result<Self, String> {
        unsafe {
            let mut cfg = sys::prism_config_init();
            let ctx = sys::prism_init(&mut cfg);
            if ctx.is_null() {
                return Err("prism_init failed".to_string());
            }
            let backend = sys::prism_registry_create_best(ctx);
            if backend.is_null() {
                sys::prism_shutdown(ctx);
                return Err("no speech backend available".to_string());
            }
            let err = sys::prism_backend_initialize(backend);
            if err != sys::PRISM_OK && err != sys::PRISM_ERROR_ALREADY_INITIALIZED {
                let msg = error_string(err);
                sys::prism_backend_free(backend);
                sys::prism_shutdown(ctx);
                return Err(format!("backend initialization failed: {msg}"));
            }
            Ok(Self { ctx, backend })
        }
    }

    /// Name of the active backend (e.g. "NVDA", "SAPI").
    pub fn backend_name(&self) -> String {
        unsafe {
            let ptr = sys::prism_backend_name(self.backend);
            if ptr.is_null() {
                return String::new();
            }
            CStr::from_ptr(ptr).to_string_lossy().into_owned()
        }
    }

    /// Speak text. `interrupt` cuts off the current utterance; pass false
    /// to queue politely.
    pub fn speak(&mut self, text: &str, interrupt: bool) -> Result<(), String> {
        let text = to_cstring(text);
        check(unsafe { sys::prism_backend_speak(self.backend, text.as_ptr(), interrupt) })
    }

    /// Send text to a braille display, if the backend supports one.
    pub fn braille(&mut self, text: &str) -> Result<(), String> {
        let text = to_cstring(text);
        check(unsafe { sys::prism_backend_braille(self.backend, text.as_ptr()) })
    }

    /// Speak and braille in one call, where supported.
    pub fn output(&mut self, text: &str, interrupt: bool) -> Result<(), String> {
        let text = to_cstring(text);
        check(unsafe { sys::prism_backend_output(self.backend, text.as_ptr(), interrupt) })
    }

    /// Interrupt the current utterance and drop queued ones. Errors are
    /// swallowed: stopping when nothing is speaking is not a fault.
    pub fn stop(&mut self) {
        unsafe {
            let _ = sys::prism_backend_stop(self.backend);
        }
    }

    /// Whether the backend reports speech in progress.
    pub fn is_speaking(&self) -> bool {
        let mut speaking = false;
        let err = unsafe { sys::prism_backend_is_speaking(self.backend, &mut speaking) };
        err == sys::PRISM_OK && speaking
    }
}

impl Drop for Speech {
    fn drop(&mut self) {
        unsafe {
            sys::prism_backend_free(self.backend);
            sys::prism_shutdown(self.ctx);
        }
    }
}

/// NUL bytes would truncate at the FFI boundary; replace them.
fn to_cstring(text: &str) -> CString {
    CString::new(text.replace('\0', " ")).expect("interior NULs replaced")
}

fn check(err: sys::PrismError) -> Result<(), String> {
    if err == sys::PRISM_OK {
        Ok(())
    } else {
        Err(error_string(err))
    }
}

fn error_string(err: sys::PrismError) -> String {
    unsafe {
        let ptr = sys::prism_error_string(err);
        if ptr.is_null() {
            format!("prism error {err}")
        } else {
            CStr::from_ptr(ptr).to_string_lossy().into_owned()
        }
    }
}
