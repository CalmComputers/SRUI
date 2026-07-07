// srui-core: retained semantic tree, focus/navigation, input vocabulary.
// Sans-IO: no TTS, no clipboard, no event loop, no file access.
// See docs/architecture.md at the workspace root.

pub mod clipboard;
pub mod editbox;
pub mod editor;
pub mod events;
pub mod focus;
pub mod input;
pub mod key_combo;
pub mod nav;
pub mod speech;
pub mod text_nav;
pub mod tree;
pub mod types;
pub mod ui;
pub mod widget;

pub use ropey::Rope;
