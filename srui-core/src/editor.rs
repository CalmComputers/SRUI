//! EditorState — pure text editing with speech feedback.
//!
//! All cursor/selection positions are **char indices** (not byte offsets).

use ropey::Rope;

use crate::speech::speak_char;
use crate::text_nav;

/// Persistent state for a text editor widget.
#[derive(Debug, Clone)]
pub struct EditorState {
    pub rope: Rope,
    /// Cursor position as a char index.
    pub cursor: usize,
    /// Selection as (anchor, cursor) in char indices. Anchor is the fixed end.
    pub selection: Option<(usize, usize)>,
    /// Sticky column for vertical line navigation (char offset from line start).
    pub preferred_column: Option<usize>,
    pub multiline: bool,
    pub read_only: bool,
}

impl EditorState {
    pub fn new(text: &str, multiline: bool) -> Self {
        Self {
            rope: Rope::from_str(text),
            cursor: 0,
            selection: None,
            preferred_column: None,
            multiline,
            read_only: false,
        }
    }

    /// Current text content as a String. Documented as O(n).
    pub fn text(&self) -> String {
        self.rope.chunks().collect()
    }

    /// Length in chars.
    pub fn len(&self) -> usize {
        self.rope.len_chars()
    }

    pub fn is_empty(&self) -> bool {
        self.rope.len_chars() == 0
    }

    /// Whether a selection is active (non-empty range).
    pub fn has_selection(&self) -> bool {
        match self.selection {
            Some((a, c)) => a != c,
            None => false,
        }
    }

    /// Collapse an active selection directionally: Left collapses to start,
    /// Right collapses to end. Returns true if there was a selection to collapse.
    pub fn collapse_selection_directional(&mut self, forward: bool) -> bool {
        if let Some((anchor, cursor)) = self.selection.take() {
            if anchor != cursor {
                self.cursor = if forward {
                    anchor.max(cursor)
                } else {
                    anchor.min(cursor)
                };
                self.preferred_column = None;
                return true;
            }
        }
        false
    }

    /// Read all text, returning "blank" if empty.
    pub fn read_all(&self) -> String {
        if self.is_empty() {
            "blank".to_string()
        } else {
            self.text()
        }
    }

    // ── Editing operations ──

    /// Insert a character at the cursor. Returns speech feedback.
    pub fn insert_char(&mut self, ch: char) -> String {
        if self.read_only {
            return String::new();
        }
        let had_selection = self.delete_selection_silent();
        let mut buf = [0u8; 4];
        let s = ch.encode_utf8(&mut buf);
        self.rope.insert(self.cursor, s);
        self.cursor += 1; // one char
        self.clear_selection();
        self.preferred_column = None;
        let char_speech = speak_char(s);
        if had_selection {
            format!("selection removed, {char_speech}")
        } else {
            char_speech
        }
    }

    /// Insert a newline at the cursor (multiline only). Returns speech feedback.
    pub fn insert_newline(&mut self) -> String {
        if !self.multiline || self.read_only {
            return String::new();
        }
        let had_selection = self.delete_selection_silent();
        self.rope.insert(self.cursor, "\n");
        self.cursor += 1; // '\n' is 1 char
        self.clear_selection();
        self.preferred_column = None;
        let nl_speech = speak_char("\n");
        if had_selection {
            format!("selection removed, {nl_speech}")
        } else {
            nl_speech
        }
    }

    /// Delete the character before the cursor. Returns speech for the deleted character.
    pub fn delete_backward(&mut self) -> Option<String> {
        if self.read_only {
            return None;
        }
        if self.delete_selection_silent() {
            return Some("deleted".to_string());
        }
        if self.cursor == 0 {
            return None;
        }
        let prev = text_nav::prev_grapheme(&self.rope, self.cursor)?;
        let deleted = text_nav::grapheme_at(&self.rope, prev)?;
        self.rope.remove(prev..self.cursor);
        self.cursor = prev;
        self.preferred_column = None;
        Some(speak_char(&deleted))
    }

    /// Delete the character after the cursor.
    pub fn delete_forward(&mut self) -> Option<String> {
        if self.read_only {
            return None;
        }
        if self.delete_selection_silent() {
            return Some("deleted".to_string());
        }
        let next = text_nav::next_grapheme(&self.rope, self.cursor)?;
        let deleted = text_nav::grapheme_at(&self.rope, self.cursor)?;
        self.rope.remove(self.cursor..next);
        self.preferred_column = None;
        Some(speak_char(&deleted))
    }

    /// Delete the word before the cursor (Notepad-style: word + trailing delimiters).
    pub fn delete_word_backward(&mut self) -> Option<String> {
        if self.read_only {
            return None;
        }
        if self.delete_selection_silent() {
            return Some("deleted".to_string());
        }
        if self.cursor == 0 {
            return None;
        }
        let target = text_nav::prev_word_extent(&self.rope, self.cursor);
        let deleted = self.rope.slice(target..self.cursor).to_string();
        self.rope.remove(target..self.cursor);
        self.cursor = target;
        self.preferred_column = None;
        Some(deleted)
    }

    /// Delete the word after the cursor (Notepad-style: word + trailing delimiters).
    pub fn delete_word_forward(&mut self) -> Option<String> {
        if self.read_only {
            return None;
        }
        if self.delete_selection_silent() {
            return Some("deleted".to_string());
        }
        let len = self.len();
        if self.cursor >= len {
            return None;
        }
        let target = text_nav::next_word_extent(&self.rope, self.cursor);
        let deleted = self.rope.slice(self.cursor..target).to_string();
        self.rope.remove(self.cursor..target);
        self.preferred_column = None;
        Some(deleted)
    }

    // ── Movement operations ──

    /// Move cursor left one grapheme. Returns the character at the new position or "blank".
    pub fn move_left(&mut self) -> String {
        if self.collapse_selection_directional(false) {
            return text_nav::grapheme_at(&self.rope, self.cursor)
                .map(|g| speak_char(&g))
                .unwrap_or_else(|| "blank".to_string());
        }
        match text_nav::prev_grapheme(&self.rope, self.cursor) {
            Some(pos) => {
                self.cursor = pos;
                self.preferred_column = None;
                text_nav::grapheme_at(&self.rope, pos)
                    .map(|g| speak_char(&g))
                    .unwrap_or_else(|| "blank".to_string())
            }
            None => "blank".to_string(),
        }
    }

    /// Move cursor right one grapheme.
    pub fn move_right(&mut self) -> String {
        if self.collapse_selection_directional(true) {
            return text_nav::grapheme_at(&self.rope, self.cursor)
                .map(|g| speak_char(&g))
                .unwrap_or_else(|| "blank".to_string());
        }
        match text_nav::next_grapheme(&self.rope, self.cursor) {
            Some(pos) => {
                self.cursor = pos;
                self.preferred_column = None;
                text_nav::grapheme_at(&self.rope, pos)
                    .map(|g| speak_char(&g))
                    .unwrap_or_else(|| "blank".to_string())
            }
            None => "blank".to_string(),
        }
    }

    /// Move cursor left by one word (Windows Ctrl+Left behaviour).
    ///
    /// Lands on the start of the current word (if not already there),
    /// or the start of the previous word.  Speaks the word landed on.
    pub fn move_word_left(&mut self) -> String {
        if self.collapse_selection_directional(false) {
            return text_nav::word_at(&self.rope, self.cursor);
        }
        let target = text_nav::prev_word_start(&self.rope, self.cursor);
        if target == self.cursor {
            return text_nav::word_at(&self.rope, self.cursor);
        }
        self.cursor = target;
        self.preferred_column = None;
        text_nav::word_at(&self.rope, self.cursor)
    }

    /// Move cursor right by one word (Windows Ctrl+Right behaviour).
    ///
    /// Lands on the start of the next word.  Speaks the word landed on.
    pub fn move_word_right(&mut self) -> String {
        if self.collapse_selection_directional(true) {
            return text_nav::word_at(&self.rope, self.cursor);
        }
        let target = text_nav::next_word_start(&self.rope, self.cursor);
        if target == self.cursor {
            return text_nav::word_at(&self.rope, self.cursor);
        }
        self.cursor = target;
        self.preferred_column = None;
        text_nav::word_at(&self.rope, self.cursor)
    }

    /// Move cursor to line start (Home).
    pub fn move_to_line_start(&mut self) -> String {
        self.clear_selection();
        let start = text_nav::line_start(&self.rope, self.cursor);
        self.cursor = start;
        self.preferred_column = None;
        text_nav::grapheme_at(&self.rope, start)
            .map(|g| speak_char(&g))
            .unwrap_or_else(|| "blank".to_string())
    }

    /// Move cursor to line end (End).
    pub fn move_to_line_end(&mut self) -> String {
        self.clear_selection();
        let end = text_nav::line_end(&self.rope, self.cursor);
        self.cursor = end;
        self.preferred_column = None;
        let line_start = text_nav::line_start(&self.rope, end);
        if end > line_start {
            match text_nav::grapheme_before(&self.rope, end) {
                Some(g) if g != "\n" => speak_char(&g),
                _ => "blank".to_string(),
            }
        } else {
            "blank".to_string()
        }
    }

    /// Move cursor to document start (Ctrl+Home).
    pub fn move_to_doc_start(&mut self) -> String {
        self.clear_selection();
        self.cursor = 0;
        self.preferred_column = None;
        text_nav::grapheme_at(&self.rope, 0)
            .map(|g| speak_char(&g))
            .unwrap_or_else(|| "blank".to_string())
    }

    /// Move cursor to document end (Ctrl+End).
    pub fn move_to_doc_end(&mut self) -> String {
        self.clear_selection();
        self.cursor = self.len();
        self.preferred_column = None;
        if self.cursor > 0 {
            text_nav::grapheme_before(&self.rope, self.cursor)
                .map(|g| {
                    if g == "\n" || g == "\r" {
                        let prev = text_nav::prev_grapheme(
                            &self.rope,
                            self.cursor.saturating_sub(1),
                        );
                        prev.and_then(|p| text_nav::grapheme_at(&self.rope, p))
                            .map(|g2| speak_char(&g2))
                            .unwrap_or_else(|| "blank".to_string())
                    } else {
                        speak_char(&g)
                    }
                })
                .unwrap_or_else(|| "blank".to_string())
        } else {
            "blank".to_string()
        }
    }

    /// Move cursor up one line (multiline only).
    pub fn move_line_up(&mut self) -> String {
        if !self.multiline {
            return self.read_all();
        }
        self.clear_selection();
        let col = self
            .preferred_column
            .unwrap_or_else(|| self.cursor - text_nav::line_start(&self.rope, self.cursor));
        match text_nav::line_up(&self.rope, self.cursor, col) {
            Some((pos, new_col)) => {
                self.cursor = pos;
                self.preferred_column = Some(new_col);
                let line = text_nav::current_line_text(&self.rope, pos);
                if line.is_empty() {
                    "blank".to_string()
                } else {
                    line
                }
            }
            None => "top".to_string(),
        }
    }

    /// Move cursor down one line (multiline only).
    pub fn move_line_down(&mut self) -> String {
        if !self.multiline {
            return self.read_all();
        }
        self.clear_selection();
        let col = self
            .preferred_column
            .unwrap_or_else(|| self.cursor - text_nav::line_start(&self.rope, self.cursor));
        match text_nav::line_down(&self.rope, self.cursor, col) {
            Some((pos, new_col)) => {
                self.cursor = pos;
                self.preferred_column = Some(new_col);
                let line = text_nav::current_line_text(&self.rope, pos);
                if line.is_empty() {
                    "blank".to_string()
                } else {
                    line
                }
            }
            None => "bottom".to_string(),
        }
    }

    // ── Selection operations ──

    pub fn select_left(&mut self) -> String {
        let anchor = self.selection_anchor();
        match text_nav::prev_grapheme(&self.rope, self.cursor) {
            Some(pos) => {
                self.cursor = pos;
                self.selection = Some((anchor, self.cursor));
                self.preferred_column = None;
                self.describe_selection()
            }
            None => "blank".to_string(),
        }
    }

    pub fn select_right(&mut self) -> String {
        let anchor = self.selection_anchor();
        match text_nav::next_grapheme(&self.rope, self.cursor) {
            Some(pos) => {
                self.cursor = pos;
                self.selection = Some((anchor, self.cursor));
                self.preferred_column = None;
                self.describe_selection()
            }
            None => "blank".to_string(),
        }
    }

    pub fn select_word_left(&mut self) -> String {
        let anchor = self.selection_anchor();
        let target = text_nav::prev_word_extent(&self.rope, self.cursor);
        if target == self.cursor {
            return "blank".to_string();
        }
        self.cursor = target;
        self.selection = Some((anchor, self.cursor));
        self.preferred_column = None;
        self.describe_selection()
    }

    pub fn select_word_right(&mut self) -> String {
        let anchor = self.selection_anchor();
        let target = text_nav::next_word_extent(&self.rope, self.cursor);
        if target == self.cursor {
            return "blank".to_string();
        }
        self.cursor = target;
        self.selection = Some((anchor, self.cursor));
        self.preferred_column = None;
        self.describe_selection()
    }

    pub fn select_to_line_start(&mut self) -> String {
        let anchor = self.selection_anchor();
        let start = text_nav::line_start(&self.rope, self.cursor);
        self.cursor = start;
        self.selection = Some((anchor, self.cursor));
        self.preferred_column = None;
        self.describe_selection()
    }

    pub fn select_to_line_end(&mut self) -> String {
        let anchor = self.selection_anchor();
        let end = text_nav::line_end(&self.rope, self.cursor);
        self.cursor = end;
        self.selection = Some((anchor, self.cursor));
        self.preferred_column = None;
        self.describe_selection()
    }

    pub fn select_to_doc_start(&mut self) -> String {
        let anchor = self.selection_anchor();
        self.cursor = 0;
        self.selection = Some((anchor, self.cursor));
        self.preferred_column = None;
        self.describe_selection()
    }

    pub fn select_to_doc_end(&mut self) -> String {
        let anchor = self.selection_anchor();
        self.cursor = self.len();
        self.selection = Some((anchor, self.cursor));
        self.preferred_column = None;
        self.describe_selection()
    }

    pub fn select_line_up(&mut self) -> String {
        if !self.multiline {
            return self.select_to_line_start();
        }
        let anchor = self.selection_anchor();
        let col = self
            .preferred_column
            .unwrap_or_else(|| self.cursor - text_nav::line_start(&self.rope, self.cursor));
        match text_nav::line_up(&self.rope, self.cursor, col) {
            Some((pos, new_col)) => {
                self.cursor = pos;
                self.selection = Some((anchor, self.cursor));
                self.preferred_column = Some(new_col);
                self.describe_selection()
            }
            None => "top".to_string(),
        }
    }

    pub fn select_line_down(&mut self) -> String {
        if !self.multiline {
            return self.select_to_line_end();
        }
        let anchor = self.selection_anchor();
        let col = self
            .preferred_column
            .unwrap_or_else(|| self.cursor - text_nav::line_start(&self.rope, self.cursor));
        match text_nav::line_down(&self.rope, self.cursor, col) {
            Some((pos, new_col)) => {
                self.cursor = pos;
                self.selection = Some((anchor, self.cursor));
                self.preferred_column = Some(new_col);
                self.describe_selection()
            }
            None => "bottom".to_string(),
        }
    }

    pub fn select_all(&mut self) -> String {
        let len = self.len();
        if len == 0 {
            return "blank".to_string();
        }
        self.selection = Some((0, len));
        self.cursor = len;
        if len > crate::speech::SPEAK_LIMIT {
            format!("{len} characters selected")
        } else {
            let text = self.text();
            format!("{text} selected")
        }
    }

    // ── Clipboard operations ──

    /// Copy selected text. Returns (clipboard_content, speech).
    pub fn copy(&self) -> (String, String) {
        match self.selected_text() {
            Some(text) => (text, "copied".to_string()),
            None => (String::new(), String::new()),
        }
    }

    /// Cut selected text. Returns (clipboard_content, speech).
    pub fn cut(&mut self) -> (String, String) {
        if self.read_only {
            return (String::new(), String::new());
        }
        match self.selected_text() {
            Some(text) => {
                self.delete_selection_silent();
                (text, "cut".to_string())
            }
            None => (String::new(), String::new()),
        }
    }

    /// Paste text at cursor. Returns speech.
    ///
    /// For single-line editors, newlines are converted to spaces and CRs are removed.
    pub fn paste(&mut self, text: &str) -> String {
        if self.read_only {
            return String::new();
        }
        let had_selection = self.delete_selection_silent();
        let insert_text;
        let text = if !self.multiline {
            insert_text = text.replace('\n', " ").replace('\r', "");
            &insert_text
        } else {
            text
        };
        self.rope.insert(self.cursor, text);
        self.cursor += text.chars().count();
        self.clear_selection();
        self.preferred_column = None;
        if had_selection {
            "selection removed, pasted".to_string()
        } else {
            "pasted".to_string()
        }
    }

    // ── Internal helpers ──

    fn clear_selection(&mut self) {
        self.selection = None;
    }

    fn selection_anchor(&self) -> usize {
        match self.selection {
            Some((anchor, _)) => anchor,
            None => self.cursor,
        }
    }

    /// Get the selected text, if any.
    pub fn selected_text(&self) -> Option<String> {
        let (anchor, cursor) = self.selection?;
        let start = anchor.min(cursor);
        let end = anchor.max(cursor);
        if start == end {
            return None;
        }
        Some(self.rope.slice(start..end).to_string())
    }

    /// Get the number of selected chars without materializing the text.
    pub fn selection_char_count(&self) -> usize {
        match self.selection {
            Some((a, c)) => {
                let start = a.min(c);
                let end = a.max(c);
                end - start
            }
            None => 0,
        }
    }

    /// Delete the current selection (if any). Returns true if something was deleted.
    pub fn delete_selection_silent(&mut self) -> bool {
        if let Some((anchor, cursor)) = self.selection.take() {
            let start = anchor.min(cursor);
            let end = anchor.max(cursor);
            if start < end {
                self.rope.remove(start..end);
                self.cursor = start;
                return true;
            }
        }
        false
    }

    fn describe_selection(&self) -> String {
        let char_count = self.selection_char_count();
        if char_count == 0 {
            return "blank".to_string();
        }
        if char_count > crate::speech::SPEAK_LIMIT {
            format!("{char_count} characters selected")
        } else {
            match self.selected_text() {
                Some(text) => format!("{text} selected"),
                None => "blank".to_string(),
            }
        }
    }

    /// Set text content (used during reconciliation to sync with app state).
    /// Uses ropey's chunk-based PartialEq — no allocation.
    pub fn set_text(&mut self, text: &str) {
        if self.rope != text {
            self.rope = Rope::from_str(text);
            self.cursor = self.cursor.min(self.rope.len_chars());
            self.selection = None;
        }
    }

    /// Set text from a Rope (used during reconciliation to sync with app state).
    /// Uses ropey's chunk-based PartialEq — no allocation, O(n) comparison
    /// only when content actually differs (which is rare during normal editing
    /// since the model's rope originates from the editor's own rope via on_change).
    pub fn set_rope(&mut self, rope: &Rope) {
        if self.rope != *rope {
            self.rope = rope.clone();
            self.cursor = self.cursor.min(self.rope.len_chars());
            self.selection = None;
        }
    }

    /// Extract a slice as a String (char range).
    pub fn slice_to_string(&self, start: usize, end: usize) -> String {
        let start = start.min(self.rope.len_chars());
        let end = end.min(self.rope.len_chars());
        if start >= end {
            return String::new();
        }
        self.rope.slice(start..end).to_string()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn insert_and_speak() {
        let mut editor = EditorState::new("", false);
        assert_eq!(editor.insert_char('h'), "h");
        assert_eq!(editor.insert_char('i'), "i");
        assert_eq!(editor.text(), "hi");
        assert_eq!(editor.cursor, 2);
    }

    #[test]
    fn insert_uppercase_speaks_cap() {
        let mut editor = EditorState::new("", false);
        assert_eq!(editor.insert_char('A'), "cap A");
    }

    #[test]
    fn insert_space_speaks_space() {
        let mut editor = EditorState::new("", false);
        assert_eq!(editor.insert_char(' '), "space");
    }

    #[test]
    fn delete_backward() {
        let mut editor = EditorState::new("abc", false);
        editor.cursor = 3; // char index, past 'c'
        assert_eq!(editor.delete_backward(), Some("c".to_string()));
        assert_eq!(editor.text(), "ab");
        assert_eq!(editor.cursor, 2);
    }

    #[test]
    fn delete_backward_at_start() {
        let mut editor = EditorState::new("abc", false);
        editor.cursor = 0;
        assert_eq!(editor.delete_backward(), None);
    }

    #[test]
    fn delete_forward() {
        let mut editor = EditorState::new("abc", false);
        editor.cursor = 0;
        assert_eq!(editor.delete_forward(), Some("a".to_string()));
        assert_eq!(editor.text(), "bc");
        assert_eq!(editor.cursor, 0);
    }

    #[test]
    fn delete_word_backward() {
        // Notepad-style: Ctrl+Backspace deletes back to the start of the
        // current word (same boundary as Ctrl+Left).
        let mut editor = EditorState::new("hello world", false);
        editor.cursor = 11; // end
        let spoken = editor.delete_word_backward().unwrap();
        assert_eq!(spoken, "world");
        assert_eq!(editor.text(), "hello ");
    }

    #[test]
    fn move_left_right() {
        let mut editor = EditorState::new("abc", false);
        editor.cursor = 1;
        assert_eq!(editor.move_left(), "a");
        assert_eq!(editor.cursor, 0);
        assert_eq!(editor.move_left(), "blank");

        assert_eq!(editor.move_right(), "b");
        assert_eq!(editor.cursor, 1);
    }

    #[test]
    fn move_word_left_right() {
        // Windows-style: Ctrl+Left lands on word starts, Ctrl+Right lands on
        // the start of the *next* word.
        let mut editor = EditorState::new("hello world", false);
        editor.cursor = 11;
        editor.move_word_left();
        assert_eq!(editor.cursor, 6); // start of "world"
        editor.move_word_left();
        assert_eq!(editor.cursor, 0); // start of "hello"

        editor.move_word_right();
        assert_eq!(editor.cursor, 6); // start of "world"
        editor.move_word_right();
        assert_eq!(editor.cursor, 11); // end of text (no next word)
    }

    #[test]
    fn move_to_line_start_end() {
        let mut editor = EditorState::new("hello", false);
        editor.cursor = 3;
        assert_eq!(editor.move_to_line_start(), "h");
        assert_eq!(editor.cursor, 0);
        assert_eq!(editor.move_to_line_end(), "o");
        assert_eq!(editor.cursor, 5);
    }

    #[test]
    fn move_to_line_end_multiline_non_last() {
        let mut editor = EditorState::new("abc\ndef\nghi", true);
        editor.cursor = 0;
        assert_eq!(editor.move_to_line_end(), "c");
        assert_eq!(editor.cursor, 3);
    }

    #[test]
    fn move_to_line_end_empty_line() {
        let mut editor = EditorState::new("abc\n\nghi", true);
        editor.cursor = 4; // empty line
        assert_eq!(editor.move_to_line_end(), "blank");
        assert_eq!(editor.cursor, 4);
    }

    #[test]
    fn move_to_doc_start_end() {
        let mut editor = EditorState::new("hello\nworld", true);
        editor.cursor = 8;
        assert_eq!(editor.move_to_doc_start(), "h");
        assert_eq!(editor.cursor, 0);
        assert_eq!(editor.move_to_doc_end(), "d");
        assert_eq!(editor.cursor, 11);
    }

    #[test]
    fn line_up_down_multiline() {
        let mut editor = EditorState::new("abc\ndef\nghi", true);
        editor.cursor = 4; // start of "def"
        assert_eq!(editor.move_line_up(), "abc");
        assert_eq!(editor.cursor, 0);
        assert_eq!(editor.move_line_up(), "top");

        assert_eq!(editor.move_line_down(), "def");
        assert_eq!(editor.move_line_down(), "ghi");
        assert_eq!(editor.move_line_down(), "bottom");
    }

    #[test]
    fn line_up_down_singleline() {
        let mut editor = EditorState::new("hello", false);
        assert_eq!(editor.move_line_up(), "hello");
        assert_eq!(editor.move_line_down(), "hello");
    }

    #[test]
    fn select_and_copy() {
        let mut editor = EditorState::new("hello", false);
        editor.cursor = 0;
        editor.select_right();
        editor.select_right();
        editor.select_right();
        assert_eq!(editor.selected_text(), Some("hel".to_string()));

        let (clip, speech) = editor.copy();
        assert_eq!(clip, "hel");
        assert_eq!(speech, "copied");
    }

    #[test]
    fn select_all() {
        let mut editor = EditorState::new("hello", false);
        let speech = editor.select_all();
        assert_eq!(speech, "hello selected");
    }

    #[test]
    fn select_all_empty() {
        let mut editor = EditorState::new("", false);
        assert_eq!(editor.select_all(), "blank");
    }

    #[test]
    fn cut() {
        let mut editor = EditorState::new("hello", false);
        editor.selection = Some((0, 3));
        editor.cursor = 3;
        let (clip, speech) = editor.cut();
        assert_eq!(clip, "hel");
        assert_eq!(speech, "cut");
        assert_eq!(editor.text(), "lo");
    }

    #[test]
    fn paste() {
        let mut editor = EditorState::new("hd", false);
        editor.cursor = 1;
        let speech = editor.paste("ello worl");
        assert_eq!(speech, "pasted");
        assert_eq!(editor.text(), "hello world");
    }

    #[test]
    fn read_all_empty() {
        let editor = EditorState::new("", false);
        assert_eq!(editor.read_all(), "blank");
    }

    #[test]
    fn read_all_with_content() {
        let editor = EditorState::new("hello", false);
        assert_eq!(editor.read_all(), "hello");
    }

    #[test]
    fn read_only_blocks_edits() {
        let mut editor = EditorState::new("hello", false);
        editor.read_only = true;
        assert_eq!(editor.insert_char('x'), "");
        assert_eq!(editor.delete_backward(), None);
        assert_eq!(editor.delete_forward(), None);
        assert_eq!(editor.text(), "hello");
    }

    #[test]
    fn select_to_line_end() {
        let mut editor = EditorState::new("hello", false);
        editor.cursor = 0;
        let speech = editor.select_to_line_end();
        assert_eq!(speech, "hello selected");
    }

    #[test]
    fn move_left_collapses_selection_to_start() {
        let mut editor = EditorState::new("hello", false);
        editor.selection = Some((1, 4));
        editor.cursor = 4;
        let speech = editor.move_left();
        assert_eq!(editor.cursor, 1);
        assert!(editor.selection.is_none());
        assert_eq!(speech, "e");
    }

    #[test]
    fn move_right_collapses_selection_to_end() {
        let mut editor = EditorState::new("hello", false);
        editor.selection = Some((1, 4));
        editor.cursor = 4;
        let speech = editor.move_right();
        assert_eq!(editor.cursor, 4);
        assert!(editor.selection.is_none());
        assert_eq!(speech, "o");
    }

    #[test]
    fn move_word_left_collapses_selection_to_start() {
        let mut editor = EditorState::new("hello world", false);
        editor.selection = Some((0, 5));
        editor.cursor = 5;
        let speech = editor.move_word_left();
        assert_eq!(editor.cursor, 0);
        assert!(editor.selection.is_none());
        assert_eq!(speech, "hello");
    }

    #[test]
    fn move_word_right_collapses_selection_to_end() {
        let mut editor = EditorState::new("hello world", false);
        editor.selection = Some((0, 5));
        editor.cursor = 5;
        let speech = editor.move_word_right();
        assert_eq!(editor.cursor, 5);
        assert!(editor.selection.is_none());
        assert_eq!(speech, " ");
    }

    #[test]
    fn paste_single_line_converts_newlines() {
        let mut editor = EditorState::new("", false);
        editor.paste("hello\nworld\r\n!");
        assert_eq!(editor.text(), "hello world !");
    }

    #[test]
    fn cursor_clamp_on_set_text() {
        let mut editor = EditorState::new("hello world", false);
        editor.cursor = 11;
        editor.set_text("hi");
        assert_eq!(editor.cursor, 2);
    }
}

#[cfg(test)]
mod proptests {
    use super::*;
    use proptest::prelude::*;

    #[derive(Debug, Clone)]
    enum EditorOp {
        InsertChar(char),
        InsertNewline,
        DeleteBackward,
        DeleteForward,
        DeleteWordBackward,
        DeleteWordForward,
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
        SelectLeft,
        SelectRight,
        SelectWordLeft,
        SelectWordRight,
        SelectToLineStart,
        SelectToLineEnd,
        SelectToDocStart,
        SelectToDocEnd,
        SelectAll,
        Paste(String),
    }

    fn arb_editor_op() -> impl Strategy<Value = EditorOp> {
        prop_oneof![
            (0x20u8..=0x7Eu8)
                .prop_map(|b| EditorOp::InsertChar(b as char)),
            Just(EditorOp::InsertNewline),
            Just(EditorOp::DeleteBackward),
            Just(EditorOp::DeleteForward),
            Just(EditorOp::DeleteWordBackward),
            Just(EditorOp::DeleteWordForward),
            Just(EditorOp::MoveLeft),
            Just(EditorOp::MoveRight),
            Just(EditorOp::MoveWordLeft),
            Just(EditorOp::MoveWordRight),
            Just(EditorOp::MoveToLineStart),
            Just(EditorOp::MoveToLineEnd),
            Just(EditorOp::MoveToDocStart),
            Just(EditorOp::MoveToDocEnd),
            Just(EditorOp::MoveLineUp),
            Just(EditorOp::MoveLineDown),
            Just(EditorOp::SelectLeft),
            Just(EditorOp::SelectRight),
            Just(EditorOp::SelectWordLeft),
            Just(EditorOp::SelectWordRight),
            Just(EditorOp::SelectToLineStart),
            Just(EditorOp::SelectToLineEnd),
            Just(EditorOp::SelectToDocStart),
            Just(EditorOp::SelectToDocEnd),
            Just(EditorOp::SelectAll),
            "[a-zA-Z0-9 ]{0,20}".prop_map(EditorOp::Paste),
        ]
    }

    fn apply_op(editor: &mut EditorState, op: &EditorOp) {
        match op {
            EditorOp::InsertChar(ch) => { editor.insert_char(*ch); }
            EditorOp::InsertNewline => { editor.insert_newline(); }
            EditorOp::DeleteBackward => { editor.delete_backward(); }
            EditorOp::DeleteForward => { editor.delete_forward(); }
            EditorOp::DeleteWordBackward => { editor.delete_word_backward(); }
            EditorOp::DeleteWordForward => { editor.delete_word_forward(); }
            EditorOp::MoveLeft => { editor.move_left(); }
            EditorOp::MoveRight => { editor.move_right(); }
            EditorOp::MoveWordLeft => { editor.move_word_left(); }
            EditorOp::MoveWordRight => { editor.move_word_right(); }
            EditorOp::MoveToLineStart => { editor.move_to_line_start(); }
            EditorOp::MoveToLineEnd => { editor.move_to_line_end(); }
            EditorOp::MoveToDocStart => { editor.move_to_doc_start(); }
            EditorOp::MoveToDocEnd => { editor.move_to_doc_end(); }
            EditorOp::MoveLineUp => { editor.move_line_up(); }
            EditorOp::MoveLineDown => { editor.move_line_down(); }
            EditorOp::SelectLeft => { editor.select_left(); }
            EditorOp::SelectRight => { editor.select_right(); }
            EditorOp::SelectWordLeft => { editor.select_word_left(); }
            EditorOp::SelectWordRight => { editor.select_word_right(); }
            EditorOp::SelectToLineStart => { editor.select_to_line_start(); }
            EditorOp::SelectToLineEnd => { editor.select_to_line_end(); }
            EditorOp::SelectToDocStart => { editor.select_to_doc_start(); }
            EditorOp::SelectToDocEnd => { editor.select_to_doc_end(); }
            EditorOp::SelectAll => { editor.select_all(); }
            EditorOp::Paste(text) => { editor.paste(text); }
        }
    }

    proptest! {
        /// Cursor is always in [0, rope.len_chars()] after any operation.
        #[test]
        fn cursor_always_in_valid_range(
            initial_text in "[a-zA-Z0-9 \n]{0,50}",
            multiline in proptest::bool::ANY,
            ops in proptest::collection::vec(arb_editor_op(), 1..50),
        ) {
            let mut editor = EditorState::new(&initial_text, multiline);

            prop_assert!(
                editor.cursor <= editor.rope.len_chars(),
                "Initial cursor {} > rope len_chars {}",
                editor.cursor,
                editor.rope.len_chars()
            );

            for (i, op) in ops.iter().enumerate() {
                apply_op(&mut editor, op);
                let len = editor.rope.len_chars();
                prop_assert!(
                    editor.cursor <= len,
                    "After op #{} ({:?}): cursor {} > rope len_chars {}",
                    i,
                    op,
                    editor.cursor,
                    len
                );
            }
        }
    }
}
