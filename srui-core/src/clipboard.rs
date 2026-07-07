//! Clipboard abstraction — injected by the host per the sans-IO rule.

/// Platform clipboard access.
pub trait Clipboard {
    fn read(&mut self) -> Option<String>;
    fn write(&mut self, text: &str);
}

/// No-op clipboard for tests and headless use.
#[derive(Debug, Default)]
pub struct NoClipboard;

impl Clipboard for NoClipboard {
    fn read(&mut self) -> Option<String> {
        None
    }
    fn write(&mut self, _text: &str) {}
}
