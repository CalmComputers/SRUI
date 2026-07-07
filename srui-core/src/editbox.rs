//! EditBox input handling — single/multi-line text editing with full
//! cursor navigation, selection, clipboard, and accessible event
//! emission.
//!
//! `handle_editbox` is a pure function over `EditorState`; the `EditBox`
//! widget in `widget.rs` wraps it. Speech for the emitted events is
//! rendered by `speech::render_event`; the tests here assert the rendered
//! strings so parity with the event payloads is checked end to end.

use smallvec::SmallVec;

use crate::clipboard::Clipboard;
use crate::editor::EditorState;
use crate::events::{
    AccessibilityEvent, Boundary, ClipboardOp, NavGranularity, SelectionKind, TypingKind,
};
use crate::input::LogicalInput;
use crate::speech::{speak_char, SPEAK_LIMIT};
use crate::text_nav;
use crate::tree::NodeId;
use ropey::Rope;

/// Result of `handle_editbox`. Carries events to dispatch and the flags
/// the widget wrapper needs: whether input was consumed (for navigation
/// fallthrough) and whether text was mutated.
#[derive(Debug, Clone, Default)]
pub struct EditboxResult {
    pub events: SmallVec<[AccessibilityEvent; 2]>,
    pub consumed: bool,
    pub changed: bool,
}

impl EditboxResult {
    /// Widget doesn't handle this input — let framework navigation take it.
    pub fn ignored() -> Self {
        Self::default()
    }

    /// Input consumed, no events, no text change.
    pub fn just_consumed() -> Self {
        Self {
            events: SmallVec::new(),
            consumed: true,
            changed: false,
        }
    }

    /// Input consumed, free-form announcement, no text change.
    pub fn announce(text: impl Into<String>) -> Self {
        let mut out = SmallVec::new();
        out.push(AccessibilityEvent::Announce { text: text.into() });
        Self {
            events: out,
            consumed: true,
            changed: false,
        }
    }

    fn push(&mut self, event: AccessibilityEvent) {
        self.events.push(event);
    }
}

// ── Handler helpers ──

/// What navigation granularity was used (determines speech context).
#[derive(Debug, Clone, Copy)]
enum NavKind {
    Char,
    Word,
    LineEdge,
    TextEdge,
    LineUp,
    LineDown,
}

impl NavKind {
    fn to_granularity(self) -> NavGranularity {
        match self {
            NavKind::Char => NavGranularity::Char,
            NavKind::Word => NavGranularity::Word,
            NavKind::LineEdge => NavGranularity::LineEdge,
            NavKind::TextEdge => NavGranularity::TextEdge,
            NavKind::LineUp => NavGranularity::LineUp,
            NavKind::LineDown => NavGranularity::LineDown,
        }
    }
}

/// Character to speak at cursor position. Falls back to the character
/// before the cursor when the cursor is past the end of text.
fn cursor_speak_char(editor: &EditorState, skip_newline: bool) -> String {
    match text_nav::grapheme_at(&editor.rope, editor.cursor) {
        Some(g) if !skip_newline || (g != "\n" && g != "\r") => speak_char(&g),
        Some(_) if editor.cursor > 0 => text_nav::grapheme_before(&editor.rope, editor.cursor)
            .map(|g| speak_char(&g))
            .unwrap_or_else(|| "blank".to_string()),
        None if editor.cursor > 0 => text_nav::grapheme_before(&editor.rope, editor.cursor)
            .map(|g| speak_char(&g))
            .unwrap_or_else(|| "blank".to_string()),
        _ => "blank".to_string(),
    }
}

/// Raw grapheme at cursor (no `speak_char` expansion). Used to populate
/// the structural `grapheme_at_cursor` field on TextNav events.
fn raw_grapheme_at_cursor(editor: &EditorState) -> String {
    text_nav::grapheme_at(&editor.rope, editor.cursor)
        .or_else(|| text_nav::grapheme_before(&editor.rope, editor.cursor))
        .unwrap_or_default()
}

/// Speak a word_at result — if it's a single non-alphanumeric char, run through speak_char.
fn speak_word(word: &str) -> String {
    let mut chars = word.chars();
    if let Some(c) = chars.next() {
        if chars.next().is_none() && !c.is_alphanumeric() {
            let mut buf = [0u8; 4];
            let s = c.encode_utf8(&mut buf);
            return speak_char(s);
        }
    }
    word.to_string()
}

/// Get the word at cursor for speech, skipping whitespace to find the nearest word.
fn word_context(editor: &EditorState) -> String {
    let pos = text_nav::skip_whitespace_forward(&editor.rope, editor.cursor);
    let w = text_nav::word_at(&editor.rope, pos);
    if w.is_empty() && editor.cursor > 0 {
        if let Some(g) = text_nav::grapheme_before(&editor.rope, editor.cursor) {
            if g == "\n" || g == "\r\n" {
                return "new line".to_string();
            }
        }
        let prev = text_nav::prev_word_boundary(&editor.rope, editor.cursor);
        let prev_pos = text_nav::skip_whitespace_forward(&editor.rope, prev);
        speak_word(&text_nav::word_at(&editor.rope, prev_pos))
    } else {
        speak_word(&w)
    }
}

/// Get the speech context for the current cursor position based on navigation kind.
fn nav_context(editor: &EditorState, kind: NavKind) -> String {
    match kind {
        NavKind::Char => cursor_speak_char(editor, false),
        NavKind::Word => word_context(editor),
        NavKind::LineEdge | NavKind::TextEdge => cursor_speak_char(editor, true),
        NavKind::LineUp | NavKind::LineDown => {
            let text = text_nav::current_line_text(&editor.rope, editor.cursor);
            if text.is_empty() {
                "blank".to_string()
            } else {
                text
            }
        }
    }
}

fn current_line(editor: &EditorState) -> String {
    text_nav::current_line_text(&editor.rope, editor.cursor)
}

fn is_top_boundary(forward: bool) -> bool {
    !forward
}

/// Whether a character is a word boundary (triggers word echo).
fn is_word_separator(ch: char) -> bool {
    ch.is_whitespace() || ch.is_ascii_punctuation()
}

/// Should a separator typed at `cursor` trigger a word-echo announcement?
///
/// Yes only when the just-typed separator is the **first** in a run —
/// i.e. the char immediately before it is a non-separator. Without
/// this guard, "hello..." would speak "hello", then "." again as each
/// trailing dot is typed (because each dot starts a fresh
/// "completed word" lookup that just reads the previous separators).
/// The user expects one announcement per word.
fn first_separator_in_run(rope: &Rope, cursor: usize) -> bool {
    if cursor < 2 {
        return false;
    }
    rope.chars_at(cursor - 1)
        .prev()
        .is_some_and(|c| !is_word_separator(c))
}

/// Extract the word just completed before the cursor (after the separator was typed).
/// Returns None if there's no word to speak.
fn completed_word(rope: &Rope, cursor: usize) -> Option<String> {
    if cursor < 2 {
        return None;
    }
    // cursor is right after the separator we just typed.
    // The word ends at cursor-1 (the separator), so look back from there.
    let word_end = cursor - 1;
    let word_start = text_nav::prev_word_boundary(rope, word_end);
    if word_start >= word_end {
        return None;
    }
    let word = rope.slice(word_start..word_end).to_string();
    let trimmed = word.trim();
    if trimmed.is_empty() {
        None
    } else {
        Some(trimmed.to_string())
    }
}

/// Classify a LogicalInput as a non-shift navigation, returning (NavKind, forward).
fn classify_nav(input: &LogicalInput) -> Option<(NavKind, bool)> {
    match input {
        LogicalInput::MoveLeft => Some((NavKind::Char, false)),
        LogicalInput::MoveRight => Some((NavKind::Char, true)),
        LogicalInput::MoveWordLeft => Some((NavKind::Word, false)),
        LogicalInput::MoveWordRight => Some((NavKind::Word, true)),
        LogicalInput::MoveToLineStart => Some((NavKind::LineEdge, false)),
        LogicalInput::MoveToLineEnd => Some((NavKind::LineEdge, true)),
        LogicalInput::MoveToDocStart => Some((NavKind::TextEdge, false)),
        LogicalInput::MoveToDocEnd => Some((NavKind::TextEdge, true)),
        LogicalInput::MoveLineUp | LogicalInput::MoveUp => Some((NavKind::LineUp, false)),
        LogicalInput::MoveLineDown | LogicalInput::MoveDown => Some((NavKind::LineDown, true)),
        _ => None,
    }
}

/// Classify a LogicalInput as a shift-selection, returning (NavKind, forward).
fn classify_select(input: &LogicalInput) -> Option<(NavKind, bool)> {
    match input {
        LogicalInput::SelectLeft => Some((NavKind::Char, false)),
        LogicalInput::SelectRight => Some((NavKind::Char, true)),
        LogicalInput::SelectWordLeft => Some((NavKind::Word, false)),
        LogicalInput::SelectWordRight => Some((NavKind::Word, true)),
        LogicalInput::SelectToLineStart => Some((NavKind::LineEdge, false)),
        LogicalInput::SelectToLineEnd => Some((NavKind::LineEdge, true)),
        LogicalInput::SelectToDocStart => Some((NavKind::TextEdge, false)),
        LogicalInput::SelectToDocEnd => Some((NavKind::TextEdge, true)),
        LogicalInput::SelectLineUp => Some((NavKind::LineUp, false)),
        LogicalInput::SelectLineDown => Some((NavKind::LineDown, true)),
        _ => None,
    }
}

/// Classify a LogicalInput as a delete operation, returning (backward, word).
fn classify_delete(input: &LogicalInput) -> Option<(bool, bool)> {
    match input {
        LogicalInput::DeleteBackward => Some((true, false)),
        LogicalInput::DeleteForward => Some((false, false)),
        LogicalInput::DeleteWordBackward => Some((true, true)),
        LogicalInput::DeleteWordForward => Some((false, true)),
        _ => None,
    }
}

fn do_nav(editor: &mut EditorState, input: &LogicalInput) {
    match input {
        LogicalInput::MoveLeft => { editor.move_left(); }
        LogicalInput::MoveRight => { editor.move_right(); }
        LogicalInput::MoveWordLeft => { editor.move_word_left(); }
        LogicalInput::MoveWordRight => { editor.move_word_right(); }
        LogicalInput::MoveToLineStart => { editor.move_to_line_start(); }
        LogicalInput::MoveToLineEnd => { editor.move_to_line_end(); }
        LogicalInput::MoveToDocStart => { editor.move_to_doc_start(); }
        LogicalInput::MoveToDocEnd => { editor.move_to_doc_end(); }
        LogicalInput::MoveLineUp | LogicalInput::MoveUp => { editor.move_line_up(); }
        LogicalInput::MoveLineDown | LogicalInput::MoveDown => { editor.move_line_down(); }
        _ => {}
    }
}

fn do_select(editor: &mut EditorState, input: &LogicalInput) {
    match input {
        LogicalInput::SelectLeft => { editor.select_left(); }
        LogicalInput::SelectRight => { editor.select_right(); }
        LogicalInput::SelectWordLeft => { editor.select_word_left(); }
        LogicalInput::SelectWordRight => { editor.select_word_right(); }
        LogicalInput::SelectToLineStart => { editor.select_to_line_start(); }
        LogicalInput::SelectToLineEnd => { editor.select_to_line_end(); }
        LogicalInput::SelectToDocStart => { editor.select_to_doc_start(); }
        LogicalInput::SelectToDocEnd => { editor.select_to_doc_end(); }
        LogicalInput::SelectLineUp => { editor.select_line_up(); }
        LogicalInput::SelectLineDown => { editor.select_line_down(); }
        _ => {}
    }
}

// ── Handler ──

/// Handle input for a focused editbox. Reads/writes EditorState directly.
/// Sets `changed = true` on the returned result when text is modified.
pub fn handle_editbox(
    node: NodeId,
    input: &LogicalInput,
    editor_state: &mut EditorState,
    clipboard: &mut dyn Clipboard,
) -> EditboxResult {
    let prev_cursor = editor_state.cursor;
    let prev_selection = editor_state.selection;
    let had_selection = editor_state.has_selection();
    let was_empty = editor_state.is_empty();

    // ── Navigation (non-shift) ──
    if let Some((nav_kind, forward)) = classify_nav(input) {
        do_nav(editor_state, input);

        let cursor_moved = editor_state.cursor != prev_cursor
            || (had_selection && !editor_state.has_selection());

        if was_empty {
            return EditboxResult::announce("No text");
        }

        let mut result = EditboxResult::just_consumed();

        if cursor_moved {
            let at_end = matches!(nav_kind, NavKind::Char | NavKind::TextEdge)
                && editor_state.cursor >= editor_state.len()
                && prev_cursor < editor_state.len();
            result.push(AccessibilityEvent::TextNav {
                node,
                line: current_line(editor_state),
                grapheme_at_cursor: raw_grapheme_at_cursor(editor_state),
                context: nav_context(editor_state, nav_kind),
                granularity: nav_kind.to_granularity(),
                boundary: if at_end { Some(Boundary::Bottom) } else { None },
            });
            result
        } else {
            let boundary = if is_top_boundary(forward) {
                Boundary::Top
            } else {
                Boundary::Bottom
            };
            result.push(AccessibilityEvent::TextNav {
                node,
                line: current_line(editor_state),
                grapheme_at_cursor: raw_grapheme_at_cursor(editor_state),
                context: nav_context(editor_state, nav_kind),
                granularity: nav_kind.to_granularity(),
                boundary: Some(boundary),
            });
            result
        }
    }
    // ── Selection (shift+nav) ──
    else if let Some((nav_kind, forward)) = classify_select(input) {
        let anchor = prev_selection.map(|(a, _)| a).unwrap_or(prev_cursor);

        do_select(editor_state, input);

        let cursor_moved = editor_state.cursor != prev_cursor;

        if was_empty {
            return EditboxResult::announce("Nothing to select");
        }

        let mut result = EditboxResult::just_consumed();

        if cursor_moved {
            let prev_dist = (prev_cursor as isize - anchor as isize).unsigned_abs();
            let new_dist = (editor_state.cursor as isize - anchor as isize).unsigned_abs();
            let is_unselecting = new_dist < prev_dist;

            let sel_start = prev_cursor.min(editor_state.cursor);
            let sel_end = prev_cursor.max(editor_state.cursor);
            let delta_len = sel_end - sel_start;
            if delta_len == 0 {
                // Selection-step that didn't change selection content — speak
                // the cursor char like a regular Char nav.
                result.push(AccessibilityEvent::TextNav {
                    node,
                    line: current_line(editor_state),
                    grapheme_at_cursor: raw_grapheme_at_cursor(editor_state),
                    context: cursor_speak_char(editor_state, false),
                    granularity: NavGranularity::Char,
                    boundary: None,
                });
                return result;
            }
            let kind = if is_unselecting {
                SelectionKind::Unselected
            } else {
                SelectionKind::Selected
            };
            let delta = if delta_len > SPEAK_LIMIT {
                format!("{delta_len} characters")
            } else {
                editor_state.slice_to_string(sel_start, sel_end)
            };
            result.push(AccessibilityEvent::Selection { node, delta, kind });
            result
        } else {
            let ctx = nav_context(editor_state, nav_kind);
            let prefix = if is_top_boundary(forward) {
                "Already selected to top"
            } else {
                "Already selected to bottom"
            };
            EditboxResult::announce(format!("{prefix}, {ctx}"))
        }
    }
    // ── Select All ──
    else if matches!(input, LogicalInput::SelectAll) {
        let _ = editor_state.select_all();
        if was_empty {
            return EditboxResult::announce("Nothing to select");
        }
        let len = editor_state.len();
        let delta = if len > SPEAK_LIMIT {
            format!("{len} characters")
        } else {
            editor_state.text()
        };
        let mut result = EditboxResult::just_consumed();
        result.push(AccessibilityEvent::Selection {
            node,
            delta,
            kind: SelectionKind::All,
        });
        result
    }
    // ── Editing: TypeChar ──
    else if let LogicalInput::TypeChar(ch) = input {
        let had_sel = editor_state.has_selection();
        let _ = editor_state.insert_char(*ch);

        let mut result = EditboxResult {
            events: SmallVec::new(),
            consumed: true,
            changed: true,
        };
        if had_sel {
            result.push(AccessibilityEvent::Selection {
                node,
                delta: String::new(),
                kind: SelectionKind::Cleared,
            });
        }
        let mut buf = [0u8; 4];
        let grapheme = ch.encode_utf8(&mut buf).to_string();

        let last_word = if is_word_separator(*ch)
            && first_separator_in_run(&editor_state.rope, editor_state.cursor)
        {
            completed_word(&editor_state.rope, editor_state.cursor)
        } else {
            None
        };

        result.push(AccessibilityEvent::Typing {
            node,
            line: current_line(editor_state),
            grapheme,
            last_word,
            kind: TypingKind::Insert,
        });
        result
    }
    // ── Editing: Enter ──
    else if matches!(input, LogicalInput::Activate) {
        if !editor_state.multiline {
            // Don't consume — let the primary widget handle it
            return EditboxResult::ignored();
        }
        let had_sel = editor_state.has_selection();
        let _ = editor_state.insert_newline();

        let mut result = EditboxResult {
            events: SmallVec::new(),
            consumed: true,
            changed: true,
        };
        if had_sel {
            result.push(AccessibilityEvent::Selection {
                node,
                delta: String::new(),
                kind: SelectionKind::Cleared,
            });
        }

        let last_word = if first_separator_in_run(&editor_state.rope, editor_state.cursor) {
            completed_word(&editor_state.rope, editor_state.cursor)
        } else {
            None
        };

        result.push(AccessibilityEvent::Typing {
            node,
            line: current_line(editor_state),
            grapheme: "\n".to_string(),
            last_word,
            kind: TypingKind::Insert,
        });
        result
    }
    // ── Editing: Delete/Backspace ──
    else if let Some((backward, word)) = classify_delete(input) {
        if editor_state.read_only {
            return EditboxResult::just_consumed();
        }
        if had_selection {
            editor_state.delete_selection_silent();
            let mut result = EditboxResult {
                events: SmallVec::new(),
                consumed: true,
                changed: true,
            };
            result.push(AccessibilityEvent::Selection {
                node,
                delta: String::new(),
                kind: SelectionKind::Cleared,
            });
            return result;
        }
        let can_delete = if backward {
            editor_state.cursor > 0
        } else {
            editor_state.cursor < editor_state.len()
        };
        if !can_delete {
            return EditboxResult::announce("Nothing to delete");
        }
        let deleted = if word {
            if backward {
                editor_state.delete_word_backward()
            } else {
                editor_state.delete_word_forward()
            }
        } else if backward {
            editor_state.delete_backward()
        } else {
            editor_state.delete_forward()
        };
        let Some(deleted_text) = deleted else {
            return EditboxResult::just_consumed();
        };

        let mut result = EditboxResult {
            events: SmallVec::new(),
            consumed: true,
            changed: true,
        };
        // For char deletes the editor returns the deleted grapheme in
        // spoken form ("dot", "cap A"); it rides in `grapheme` and the
        // renderer's speak_char passes multi-char strings through.
        let (kind, grapheme, last_word) = if word {
            (TypingKind::DeleteWord, String::new(), Some(deleted_text))
        } else {
            (TypingKind::Delete, deleted_text, None)
        };
        result.push(AccessibilityEvent::Typing {
            node,
            line: current_line(editor_state),
            grapheme,
            last_word,
            kind,
        });
        result
    }
    // ── Clipboard: Copy ──
    else if matches!(input, LogicalInput::Copy) {
        if !had_selection {
            return EditboxResult::just_consumed();
        }
        let (clip, _) = editor_state.copy();
        if !clip.is_empty() {
            clipboard.write(&clip);
        }
        let mut result = EditboxResult::just_consumed();
        result.push(AccessibilityEvent::Clipboard {
            node,
            op: ClipboardOp::Copy,
        });
        result
    }
    // ── Clipboard: Cut ──
    else if matches!(input, LogicalInput::Cut) {
        if editor_state.read_only || !had_selection {
            return EditboxResult::just_consumed();
        }
        let (clip, _) = editor_state.cut();
        if !clip.is_empty() {
            clipboard.write(&clip);
        }
        let mut result = EditboxResult {
            events: SmallVec::new(),
            consumed: true,
            changed: true,
        };
        result.push(AccessibilityEvent::Clipboard {
            node,
            op: ClipboardOp::Cut,
        });
        result
    }
    // ── Clipboard: Paste ──
    else if matches!(input, LogicalInput::Paste) {
        if editor_state.read_only {
            return EditboxResult::just_consumed();
        }
        let text = clipboard.read().unwrap_or_default();
        if text.is_empty() {
            return EditboxResult::just_consumed();
        }
        let _ = editor_state.paste(&text);
        let mut result = EditboxResult {
            events: SmallVec::new(),
            consumed: true,
            changed: true,
        };
        result.push(AccessibilityEvent::Clipboard {
            node,
            op: ClipboardOp::Paste,
        });
        result
    } else {
        EditboxResult::ignored()
    }
}

// ── Label value helper ──

/// Compute the label value for an edit box.
/// Shows selection info if selected, otherwise the current line at cursor.
pub fn editbox_label_value(editor: &EditorState) -> String {
    if editor.has_selection() {
        let (anchor, cursor) = editor.selection.unwrap();
        let start = anchor.min(cursor);
        let end = anchor.max(cursor);
        let len = end - start;
        if len >= SPEAK_LIMIT {
            format!("selected {len} characters")
        } else {
            let text = editor.rope.slice(start..end).to_string();
            format!("selected {text}")
        }
    } else if editor.rope.len_chars() == 0 {
        "blank".to_string()
    } else {
        let line = text_nav::current_line_text(&editor.rope, editor.cursor);
        if line.is_empty() {
            "blank".to_string()
        } else {
            line
        }
    }
}

// ── Tests ──

#[cfg(test)]
mod tests {
    use super::*;
    use crate::clipboard::NoClipboard;
    use crate::speech::render_event;

    fn node() -> NodeId {
        NodeId::default()
    }

    /// Test helper: render speech for a result's events.
    fn speech(r: &EditboxResult) -> Vec<String> {
        r.events.iter().filter_map(render_event).collect()
    }

    fn handle(input: &LogicalInput, editor: &mut EditorState) -> EditboxResult {
        handle_editbox(node(), input, editor, &mut NoClipboard)
    }

    #[test]
    fn editbox_type_char() {
        let mut editor = EditorState::new("", false);
        let result = handle(&LogicalInput::TypeChar('h'), &mut editor);
        assert_eq!(speech(&result), &["h"]);
        assert!(result.changed);
        assert_eq!(editor.text(), "h");
    }

    #[test]
    fn editbox_right_arrow_mid_text() {
        let mut editor = EditorState::new("abc", false);
        editor.cursor = 0;
        let result = handle(&LogicalInput::MoveRight, &mut editor);
        assert_eq!(speech(&result), &["b"]);
        assert_eq!(editor.cursor, 1);
        assert!(!result.changed);
    }

    #[test]
    fn editbox_left_at_start_boundary() {
        let mut editor = EditorState::new("az", false);
        editor.cursor = 0;
        let result = handle(&LogicalInput::MoveLeft, &mut editor);
        assert_eq!(speech(&result), &["Top, a"]);
    }

    #[test]
    fn editbox_right_to_end_speaks_bottom() {
        let mut editor = EditorState::new("ab", false);
        editor.cursor = 1;
        let result = handle(&LogicalInput::MoveRight, &mut editor);
        assert_eq!(speech(&result), &["Bottom, b"]);
    }

    #[test]
    fn editbox_enter_multiline() {
        let mut editor = EditorState::new("", true);
        let result = handle(&LogicalInput::Activate, &mut editor);
        assert_eq!(speech(&result), &["new line"]);
        assert!(result.changed);
    }

    #[test]
    fn editbox_enter_singleline_not_consumed() {
        let mut editor = EditorState::new("", false);
        let result = handle(&LogicalInput::Activate, &mut editor);
        assert!(!result.consumed);
    }

    #[test]
    fn editbox_delete_backward() {
        let mut editor = EditorState::new("ab", false);
        editor.cursor = 2;
        let result = handle(&LogicalInput::DeleteBackward, &mut editor);
        assert!(result.changed);
        assert_eq!(editor.text(), "a");
    }

    #[test]
    fn editbox_end_single_line_no_bottom_first_press() {
        let mut editor = EditorState::new("hello", false);
        editor.cursor = 0;
        let result = handle(&LogicalInput::MoveToLineEnd, &mut editor);
        assert_eq!(editor.cursor, 5);
        assert!(
            !speech(&result)[0].contains("Bottom"),
            "First End press should not say Bottom, got: {:?}",
            speech(&result)
        );
    }

    #[test]
    fn editbox_end_single_line_bottom_on_repeat() {
        let mut editor = EditorState::new("hello", false);
        editor.cursor = 5;
        let result = handle(&LogicalInput::MoveToLineEnd, &mut editor);
        assert!(
            speech(&result)[0].starts_with("Bottom"),
            "Repeated End should say Bottom, got: {:?}",
            speech(&result)
        );
    }

    #[test]
    fn editbox_unhandled_input_ignored() {
        let mut editor = EditorState::new("abc", false);
        let result = handle(&LogicalInput::SpeakFocus, &mut editor);
        assert!(result.events.is_empty());
        assert!(!result.changed);
    }

    #[test]
    fn editbox_label_value_empty() {
        let editor = EditorState::new("", false);
        assert_eq!(editbox_label_value(&editor), "blank");
    }

    #[test]
    fn editbox_label_value_current_line() {
        let mut editor = EditorState::new("hello world\nsecond line", true);
        assert_eq!(editbox_label_value(&editor), "hello world");
        editor.cursor = 12; // on second line
        assert_eq!(editbox_label_value(&editor), "second line");
    }

    #[test]
    fn editbox_label_value_selection() {
        let mut editor = EditorState::new("hello world", false);
        editor.selection = Some((0, 5));
        editor.cursor = 5;
        assert_eq!(editbox_label_value(&editor), "selected hello");
    }

    #[test]
    fn editbox_label_value_single_line() {
        let editor = EditorState::new("hello world", false);
        assert_eq!(editbox_label_value(&editor), "hello world");
    }

    // ── Typing events ──
    //
    // Typing always emits a single Typing event whose rendered speech is
    // `{char}` and, on word boundaries, `{word} {char}`.

    #[test]
    fn type_char_word_boundary_speaks_word_then_char() {
        let mut editor = EditorState::new("hello", false);
        editor.cursor = 5;
        let result = handle(&LogicalInput::TypeChar(' '), &mut editor);
        assert_eq!(speech(&result), &["hello space"]);
    }

    #[test]
    fn type_char_mid_word_no_word_payload() {
        let mut editor = EditorState::new("hel", false);
        editor.cursor = 3;
        let result = handle(&LogicalInput::TypeChar('l'), &mut editor);
        assert_eq!(speech(&result), &["l"]);
        match &result.events[0] {
            AccessibilityEvent::Typing { last_word, .. } => assert!(last_word.is_none()),
            other => panic!("expected Typing, got {:?}", other),
        }
    }

    #[test]
    fn enter_after_word_speaks_word_then_newline() {
        let mut editor = EditorState::new("hello", true);
        editor.cursor = 5;
        let result = handle(&LogicalInput::Activate, &mut editor);
        assert_eq!(speech(&result), &["hello new line"]);
    }

    #[test]
    fn first_separator_only_in_run() {
        // After typing "hello.", word echo should fire once. Typing
        // additional separators in a row must NOT re-announce the word.
        let mut editor = EditorState::new("hello", false);
        editor.cursor = 5;

        let result = handle(&LogicalInput::TypeChar('.'), &mut editor);
        assert_eq!(speech(&result), &["hello dot"]);

        let result = handle(&LogicalInput::TypeChar('.'), &mut editor);
        assert_eq!(speech(&result), &["dot"]);

        let result = handle(&LogicalInput::TypeChar('.'), &mut editor);
        assert_eq!(speech(&result), &["dot"]);

        let result = handle(&LogicalInput::TypeChar(' '), &mut editor);
        assert_eq!(speech(&result), &["space"]);

        for ch in ['w', 'o', 'r', 'l', 'd'] {
            let _ = handle(&LogicalInput::TypeChar(ch), &mut editor);
        }
        let result = handle(&LogicalInput::TypeChar('!'), &mut editor);
        assert_eq!(speech(&result), &["world bang"]);
    }

    #[test]
    fn enter_after_space_doesnt_repeat_word() {
        let mut editor = EditorState::new("hello ", true);
        editor.cursor = 6;
        let result = handle(&LogicalInput::Activate, &mut editor);
        assert_eq!(speech(&result), &["new line"]);
    }

    #[test]
    fn type_over_selection_emits_clear_then_typing() {
        let mut editor = EditorState::new("hi", false);
        editor.selection = Some((0, 2));
        editor.cursor = 2;
        let result = handle(&LogicalInput::TypeChar('x'), &mut editor);
        assert_eq!(speech(&result), &["Selection removed", "x"]);
        assert!(matches!(
            result.events[0],
            AccessibilityEvent::Selection {
                kind: SelectionKind::Cleared,
                ..
            }
        ));
        assert!(matches!(
            result.events[1],
            AccessibilityEvent::Typing {
                kind: TypingKind::Insert,
                ..
            }
        ));
    }

    #[test]
    fn delete_with_selection_emits_only_clear() {
        let mut editor = EditorState::new("hi", false);
        editor.selection = Some((0, 2));
        editor.cursor = 2;
        let result = handle(&LogicalInput::DeleteBackward, &mut editor);
        assert_eq!(speech(&result), &["Selection removed"]);
        assert_eq!(editor.text(), "");
    }

    #[test]
    fn delete_word_emits_typing_deleteword() {
        let mut editor = EditorState::new("hello world", false);
        editor.cursor = 11;
        let result = handle(&LogicalInput::DeleteWordBackward, &mut editor);
        match &result.events[0] {
            AccessibilityEvent::Typing {
                kind,
                grapheme,
                last_word,
                ..
            } => {
                assert_eq!(*kind, TypingKind::DeleteWord);
                assert_eq!(grapheme, "");
                assert!(last_word.is_some());
            }
            other => panic!("expected Typing(DeleteWord), got {:?}", other),
        }
    }

    #[test]
    fn copy_emits_clipboard_event() {
        let mut editor = EditorState::new("hello", false);
        editor.selection = Some((0, 5));
        editor.cursor = 5;
        let result = handle(&LogicalInput::Copy, &mut editor);
        assert_eq!(speech(&result), &["Copy"]);
        assert!(matches!(
            result.events[0],
            AccessibilityEvent::Clipboard {
                op: ClipboardOp::Copy,
                ..
            }
        ));
    }

    #[test]
    fn select_all_emits_selection_all() {
        let mut editor = EditorState::new("hello", false);
        let result = handle(&LogicalInput::SelectAll, &mut editor);
        assert_eq!(speech(&result), &["hello selected"]);
        assert!(matches!(
            result.events[0],
            AccessibilityEvent::Selection {
                kind: SelectionKind::All,
                ..
            }
        ));
    }

    #[test]
    fn selection_delta_speech() {
        let mut editor = EditorState::new("hello", false);
        editor.cursor = 0;
        let result = handle(&LogicalInput::SelectRight, &mut editor);
        assert_eq!(speech(&result), &["h selected"]);
        let result = handle(&LogicalInput::SelectRight, &mut editor);
        assert_eq!(speech(&result), &["e selected"]);
        // Shrink the selection from the cursor side.
        let result = handle(&LogicalInput::SelectLeft, &mut editor);
        assert_eq!(speech(&result), &["e unselected"]);
    }

    #[test]
    fn completed_word_basic() {
        let rope = Rope::from("hello ");
        assert_eq!(completed_word(&rope, 6), Some("hello".to_string()));
    }

    #[test]
    fn completed_word_no_word() {
        let rope = Rope::from(" ");
        assert_eq!(completed_word(&rope, 1), None);
    }

    #[test]
    fn completed_word_punctuation_boundary() {
        let rope = Rope::from("hello.");
        assert_eq!(completed_word(&rope, 6), Some("hello".to_string()));
    }
}
