//! Logical input — the semantic layer between physical keys and widget behavior.
//!
//! Application-level shortcuts (command palettes, task switching, media
//! control) are host policy: they arrive as `RawKey` and are resolved by
//! the host's binding layer, not by variants here.

use crate::key_combo::KeyCombo;

/// Semantic input events, independent of physical key bindings.
#[derive(Debug, Clone, PartialEq)]
pub enum LogicalInput {
    // Navigation (framework-handled)
    NavigateNext,
    NavigatePrev,
    TreeUp,
    TreeDown,
    TreeLeft,
    TreeRight,
    Shortcut(char),

    // Widget input (routed to focused widget)
    Activate,
    SecondaryActivate,
    MoveUp,
    MoveDown,
    MoveLeft,
    MoveRight,
    MoveWordLeft,
    MoveWordRight,
    MoveToLineStart,
    MoveToLineEnd,
    MoveToDocStart,
    MoveToDocEnd,
    MoveLineUp,
    MoveLineDown,

    // Selection (Shift+movement)
    SelectLeft,
    SelectRight,
    SelectWordLeft,
    SelectWordRight,
    SelectToLineStart,
    SelectToLineEnd,
    SelectToDocStart,
    SelectToDocEnd,
    SelectLineUp,
    SelectLineDown,
    SelectAll,

    // Text editing
    TypeChar(char),
    DeleteBackward,
    DeleteForward,
    DeleteWordBackward,
    DeleteWordForward,

    // Clipboard
    Copy,
    Cut,
    Paste,

    // Read content (non-mutating)
    SpeakFocus,

    // Modal dismiss
    Dismiss,

    // Raw key combo (no semantic mapping found by input mapper)
    RawKey(KeyCombo),
}
