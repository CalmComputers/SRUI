//! Pure text navigation algorithms over ropey::Rope, grapheme-aware.
//!
//! All positions are **char indices** (not byte offsets).

use ropey::Rope;
use unicode_segmentation::{GraphemeCursor, GraphemeIncomplete};

// ── Grapheme helpers using GraphemeCursor on rope chunks (zero alloc) ──

/// Move to the previous grapheme cluster boundary (char index).
/// Uses GraphemeCursor operating on rope chunks — zero allocation for boundary finding.
pub fn prev_grapheme(rope: &Rope, char_pos: usize) -> Option<usize> {
    if char_pos == 0 {
        return None;
    }
    let byte_pos = rope.char_to_byte(char_pos);
    let byte_len = rope.len_bytes();
    let mut gc = GraphemeCursor::new(byte_pos, byte_len, true);

    let (mut chunk, mut chunk_start, _, _) = rope.chunk_at_byte(byte_pos);
    loop {
        match gc.prev_boundary(chunk, chunk_start) {
            Ok(Some(prev_byte)) => return Some(rope.byte_to_char(prev_byte)),
            Ok(None) => return None,
            Err(GraphemeIncomplete::PrevChunk) => {
                if chunk_start == 0 {
                    return None;
                }
                let result = rope.chunk_at_byte(chunk_start - 1);
                chunk = result.0;
                chunk_start = result.1;
            }
            Err(GraphemeIncomplete::PreContext(n)) => {
                let (ctx_chunk, ctx_start, _, _) =
                    rope.chunk_at_byte(if n > 0 { n - 1 } else { 0 });
                gc.provide_context(ctx_chunk, ctx_start);
            }
            Err(_) => return None,
        }
    }
}

/// Move to the next grapheme cluster boundary (char index).
/// Uses GraphemeCursor operating on rope chunks — zero allocation for boundary finding.
pub fn next_grapheme(rope: &Rope, char_pos: usize) -> Option<usize> {
    let char_len = rope.len_chars();
    if char_pos >= char_len {
        return None;
    }
    let byte_pos = rope.char_to_byte(char_pos);
    let byte_len = rope.len_bytes();
    let mut gc = GraphemeCursor::new(byte_pos, byte_len, true);

    let (mut chunk, mut chunk_start, _, _) = rope.chunk_at_byte(byte_pos);
    loop {
        match gc.next_boundary(chunk, chunk_start) {
            Ok(Some(next_byte)) => return Some(rope.byte_to_char(next_byte)),
            Ok(None) => return None,
            Err(GraphemeIncomplete::NextChunk) => {
                let next_start = chunk_start + chunk.len();
                if next_start >= byte_len {
                    return None;
                }
                let result = rope.chunk_at_byte(next_start);
                chunk = result.0;
                chunk_start = result.1;
            }
            Err(GraphemeIncomplete::PreContext(n)) => {
                let (ctx_chunk, ctx_start, _, _) =
                    rope.chunk_at_byte(if n > 0 { n - 1 } else { 0 });
                gc.provide_context(ctx_chunk, ctx_start);
            }
            Err(_) => return None,
        }
    }
}

/// Get the grapheme cluster string at a char position.
pub fn grapheme_at(rope: &Rope, char_pos: usize) -> Option<String> {
    if char_pos >= rope.len_chars() {
        return None;
    }
    let next = next_grapheme(rope, char_pos)?;
    Some(rope.slice(char_pos..next).to_string())
}

/// Get the grapheme cluster string before a char position.
pub fn grapheme_before(rope: &Rope, char_pos: usize) -> Option<String> {
    if char_pos == 0 {
        return None;
    }
    let prev = prev_grapheme(rope, char_pos)?;
    Some(rope.slice(prev..char_pos).to_string())
}

// ── Character class helpers ──

/// Whether a character is an identifier character (alphanumeric or underscore).
fn is_word_char(c: char) -> bool {
    c.is_alphanumeric() || c == '_'
}

/// Whether a character is punctuation (non-word, non-whitespace).
fn is_punct(c: char) -> bool {
    !is_word_char(c) && !c.is_whitespace()
}

// ── Word boundary navigation ──
// All positions are char indices.

/// Move to the previous word boundary (code-editor style).
///
/// Algorithm: consume one run of identifier or punctuation chars backward,
/// then consume any whitespace before it.
///
/// O(log n) seek, O(k) scan where k is one word + whitespace.
pub fn prev_word_boundary(rope: &Rope, char_pos: usize) -> usize {
    if char_pos == 0 {
        return 0;
    }

    let mut chars_iter = rope.chars_at(char_pos);
    let mut chars_back: usize = 0;

    let first = match chars_iter.prev() {
        Some(c) => c,
        None => return 0,
    };
    chars_back += 1;

    if is_word_char(first) {
        loop {
            match chars_iter.prev() {
                Some(c) if is_word_char(c) => chars_back += 1,
                Some(c) if c.is_whitespace() => {
                    chars_back += 1;
                    consume_ws_backward_chars(&mut chars_iter, &mut chars_back);
                    break;
                }
                _ => break,
            }
        }
    } else if is_punct(first) {
        loop {
            match chars_iter.prev() {
                Some(c) if is_punct(c) => chars_back += 1,
                Some(c) if c.is_whitespace() => {
                    chars_back += 1;
                    consume_ws_backward_chars(&mut chars_iter, &mut chars_back);
                    break;
                }
                _ => break,
            }
        }
    } else {
        // First char was whitespace
        loop {
            match chars_iter.prev() {
                Some(c) if c.is_whitespace() => chars_back += 1,
                Some(c) if is_word_char(c) => {
                    chars_back += 1;
                    loop {
                        match chars_iter.prev() {
                            Some(c2) if is_word_char(c2) => chars_back += 1,
                            _ => break,
                        }
                    }
                    break;
                }
                Some(c) if is_punct(c) => {
                    chars_back += 1;
                    loop {
                        match chars_iter.prev() {
                            Some(c2) if is_punct(c2) => chars_back += 1,
                            _ => break,
                        }
                    }
                    break;
                }
                _ => break,
            }
        }
    }

    char_pos - chars_back
}

/// Helper: consume whitespace chars backward, counting chars.
fn consume_ws_backward_chars(
    chars_iter: &mut ropey::iter::Chars<'_>,
    chars_back: &mut usize,
) {
    loop {
        match chars_iter.prev() {
            Some(c) if c.is_whitespace() => *chars_back += 1,
            _ => break,
        }
    }
}

/// Move to the next word boundary (code-editor style).
///
/// Algorithm: consume any whitespace at cursor, then consume one run of
/// identifier or punctuation chars.
///
/// O(log n) seek, O(k) scan where k is whitespace + one word.
pub fn next_word_boundary(rope: &Rope, char_pos: usize) -> usize {
    let char_len = rope.len_chars();
    if char_pos >= char_len {
        return char_len;
    }

    let mut chars_iter = rope.chars_at(char_pos);
    let mut char_offset: usize = 0;

    // Phase 1: skip whitespace
    let first_nonws = loop {
        match chars_iter.next() {
            Some(c) if c.is_whitespace() => char_offset += 1,
            Some(c) => break c,
            None => return char_pos,
        }
    };

    // Phase 2: consume one run of word or punctuation chars
    char_offset += 1;

    if is_word_char(first_nonws) {
        loop {
            match chars_iter.next() {
                Some(c) if is_word_char(c) => char_offset += 1,
                _ => break,
            }
        }
    } else {
        loop {
            match chars_iter.next() {
                Some(c) if is_punct(c) => char_offset += 1,
                _ => break,
            }
        }
    }

    char_pos + char_offset
}

// ── Word-start navigation (code-editor style) ──
// These land on *word starts* treating word/punct/whitespace as separate
// groups. Used for non-selection Ctrl+Arrow movement.

/// Move left to the start of the current word (if not already there),
/// or the start of the previous word.  Windows Ctrl+Left behaviour.
pub fn prev_word_start(rope: &Rope, char_pos: usize) -> usize {
    if char_pos == 0 {
        return 0;
    }

    let mut pos = char_pos;
    let mut chars = rope.chars_at(pos);

    // Phase 1: skip whitespace immediately before cursor
    loop {
        match chars.prev() {
            Some(c) if c.is_whitespace() => pos -= 1,
            Some(_) => {
                // undo the non-ws peek
                chars.next();
                break;
            }
            None => return 0,
        }
    }

    // Phase 2: skip the word (or punctuation run) we're now at the end of
    match chars.prev() {
        Some(c) if is_word_char(c) => {
            pos -= 1;
            loop {
                match chars.prev() {
                    Some(c2) if is_word_char(c2) => pos -= 1,
                    _ => break,
                }
            }
        }
        Some(c) if is_punct(c) => {
            pos -= 1;
            loop {
                match chars.prev() {
                    Some(c2) if is_punct(c2) => pos -= 1,
                    _ => break,
                }
            }
        }
        _ => {}
    }

    pos
}

/// Move right to the start of the next word.  Windows Ctrl+Right behaviour.
///
/// Skips the remainder of the current word/punctuation run, then any
/// whitespace, landing on the first character of the next word.
/// If no next word exists, lands at the end of the rope.
pub fn next_word_start(rope: &Rope, char_pos: usize) -> usize {
    let char_len = rope.len_chars();
    if char_pos >= char_len {
        return char_len;
    }

    let mut chars = rope.chars_at(char_pos);
    let mut pos = char_pos;

    // Phase 1: skip the current word/punctuation run
    match chars.next() {
        Some(c) if is_word_char(c) => {
            pos += 1;
            loop {
                match chars.next() {
                    Some(c2) if is_word_char(c2) => pos += 1,
                    _ => break,
                }
            }
        }
        Some(c) if is_punct(c) => {
            pos += 1;
            loop {
                match chars.next() {
                    Some(c2) if is_punct(c2) => pos += 1,
                    _ => break,
                }
            }
        }
        Some(c) if c.is_whitespace() => {
            pos += 1;
        }
        // Every char is word, punct, or whitespace — guards above are exhaustive
        Some(_) => unreachable!(),
        None => return char_len,
    }

    // Phase 2: skip whitespace to reach the next word start
    // (re-seat the iterator since phase 1 consumed one char past the run)
    let mut chars = rope.chars_at(pos);
    loop {
        match chars.next() {
            Some(c) if c.is_whitespace() => pos += 1,
            _ => break,
        }
    }

    pos
}

// ── Word-extent navigation (Notepad style) ──
// A word "extent" = word + all trailing non-word chars (punctuation +
// whitespace). Used for Ctrl+Shift+Arrow selection and Ctrl+Backspace/Delete.

/// Move left to the start of the previous word extent (Notepad-style).
///
/// Skips backward over all non-word chars (whitespace + punctuation),
/// then over the word itself, landing on its first character.
pub fn prev_word_extent(rope: &Rope, char_pos: usize) -> usize {
    if char_pos == 0 {
        return 0;
    }

    let mut pos = char_pos;
    let mut chars = rope.chars_at(pos);

    // Phase 1: skip backward over non-word chars (whitespace + punctuation)
    loop {
        match chars.prev() {
            Some(c) if !is_word_char(c) => pos -= 1,
            Some(_) => {
                // undo the word-char peek
                chars.next();
                break;
            }
            None => return 0,
        }
    }

    // Phase 2: skip backward over word chars
    loop {
        match chars.prev() {
            Some(c) if is_word_char(c) => pos -= 1,
            _ => break,
        }
    }

    pos
}

/// Move right to the start of the next word extent (Notepad-style).
///
/// If on a word char, skips the rest of the word, then skips all
/// trailing non-word chars (punctuation + whitespace).
/// If on a non-word char, skips all non-word chars.
/// Lands on the first character of the next word, or end of rope.
pub fn next_word_extent(rope: &Rope, char_pos: usize) -> usize {
    let char_len = rope.len_chars();
    if char_pos >= char_len {
        return char_len;
    }

    let mut chars = rope.chars_at(char_pos);
    let mut pos = char_pos;

    // Phase 1: if on a word char, skip the rest of the word
    match chars.next() {
        Some(c) if is_word_char(c) => {
            pos += 1;
            loop {
                match chars.next() {
                    Some(c2) if is_word_char(c2) => pos += 1,
                    _ => break,
                }
            }
        }
        Some(_) => {
            pos += 1;
        }
        None => return char_len,
    }

    // Phase 2: skip all non-word chars (punctuation + whitespace)
    let mut chars = rope.chars_at(pos);
    loop {
        match chars.next() {
            Some(c) if !is_word_char(c) => pos += 1,
            _ => break,
        }
    }

    pos
}

// ── Line navigation ──
// All positions are char indices.

/// Get the char index of the start of the line containing `char_pos`.
pub fn line_start(rope: &Rope, char_pos: usize) -> usize {
    if char_pos == 0 || rope.len_chars() == 0 {
        return 0;
    }
    let clamped = char_pos.min(rope.len_chars());
    let line_idx = rope.char_to_line(clamped);
    rope.line_to_char(line_idx)
}

/// Get the char index of the end of the line containing `char_pos` (before newline or EOF).
pub fn line_end(rope: &Rope, char_pos: usize) -> usize {
    let char_len = rope.len_chars();
    if char_pos >= char_len {
        return char_len;
    }
    let line_idx = rope.char_to_line(char_pos);
    let line = rope.line(line_idx);
    let line_char_start = rope.line_to_char(line_idx);
    let line_len = line.len_chars();

    // Strip trailing newline(s)
    let mut end = line_char_start + line_len;
    if line_len > 0 {
        let last_char = rope.char(end - 1);
        if last_char == '\n' {
            end -= 1;
            if end > line_char_start && rope.char(end - 1) == '\r' {
                end -= 1;
            }
        } else if last_char == '\r' {
            end -= 1;
        }
    }
    end
}

/// Move up one line, trying to preserve column position.
/// Returns (new_char_pos, new_column) where column is char offset from line start.
pub fn line_up(rope: &Rope, char_pos: usize, preferred_column: usize) -> Option<(usize, usize)> {
    let clamped = char_pos.min(rope.len_chars());
    let current_line_idx = rope.char_to_line(clamped);
    if current_line_idx == 0 {
        return None;
    }
    let prev_line_start = rope.line_to_char(current_line_idx - 1);
    let prev_line_end = line_end(rope, prev_line_start);
    let prev_line_len = prev_line_end.saturating_sub(prev_line_start);

    let col = preferred_column.min(prev_line_len);
    Some((prev_line_start + col, col))
}

/// Move down one line, trying to preserve column position.
/// Returns (new_char_pos, new_column) where column is char offset from line start.
pub fn line_down(
    rope: &Rope,
    char_pos: usize,
    preferred_column: usize,
) -> Option<(usize, usize)> {
    let clamped = char_pos.min(rope.len_chars());
    let current_line_idx = rope.char_to_line(clamped);
    let num_lines = rope.len_lines();
    if current_line_idx + 1 >= num_lines {
        return None;
    }
    let next_line_start = rope.line_to_char(current_line_idx + 1);
    let next_line_end = line_end(rope, next_line_start);
    let next_line_len = next_line_end.saturating_sub(next_line_start);

    let col = preferred_column.min(next_line_len);
    Some((next_line_start + col, col))
}

/// Extract the text of the line containing `char_pos` (without trailing newline).
pub fn current_line_text(rope: &Rope, char_pos: usize) -> String {
    let start = line_start(rope, char_pos);
    let end = line_end(rope, char_pos);
    if start >= end {
        return String::new();
    }
    rope.slice(start..end).to_string()
}

/// Skip whitespace forward from `char_pos`, returning the char index of the first
/// non-whitespace character.
pub fn skip_whitespace_forward(rope: &Rope, char_pos: usize) -> usize {
    let char_len = rope.len_chars();
    if char_pos >= char_len {
        return char_pos;
    }
    let mut chars = rope.chars_at(char_pos);
    let mut offset = 0;
    loop {
        match chars.next() {
            Some(c) if c.is_whitespace() => offset += 1,
            _ => break,
        }
    }
    char_pos + offset
}

/// Extract word at char position (the word surrounding or starting at pos).
pub fn word_at(rope: &Rope, char_pos: usize) -> String {
    let char_len = rope.len_chars();
    if char_pos >= char_len {
        return String::new();
    }

    let cursor_char = rope.char(char_pos);

    if !is_word_char(cursor_char) {
        return cursor_char.to_string();
    }

    // Scan backward to find the start of the word
    let mut start_char_idx = char_pos;
    if start_char_idx > 0 {
        let mut back_iter = rope.chars_at(start_char_idx);
        loop {
            match back_iter.prev() {
                Some(c) if is_word_char(c) => {
                    start_char_idx -= 1;
                }
                _ => break,
            }
        }
    }

    // Scan forward to find the end of the word
    let mut end_char_idx = char_pos + 1;
    let mut fwd_iter = rope.chars_at(end_char_idx);
    loop {
        match fwd_iter.next() {
            Some(c) if is_word_char(c) => {
                end_char_idx += 1;
            }
            _ => break,
        }
    }

    rope.slice(start_char_idx..end_char_idx).to_string()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn rope(s: &str) -> Rope {
        Rope::from_str(s)
    }

    #[test]
    fn prev_next_grapheme_ascii() {
        let r = rope("hello");
        // All ASCII so char index == byte index
        assert_eq!(prev_grapheme(&r, 0), None);
        assert_eq!(prev_grapheme(&r, 1), Some(0));
        assert_eq!(prev_grapheme(&r, 5), Some(4));

        assert_eq!(next_grapheme(&r, 0), Some(1));
        assert_eq!(next_grapheme(&r, 4), Some(5));
        assert_eq!(next_grapheme(&r, 5), None);
    }

    #[test]
    fn grapheme_at_and_before() {
        let r = rope("abc");
        assert_eq!(grapheme_at(&r, 0), Some("a".into()));
        assert_eq!(grapheme_at(&r, 2), Some("c".into()));
        assert_eq!(grapheme_at(&r, 3), None);

        assert_eq!(grapheme_before(&r, 0), None);
        assert_eq!(grapheme_before(&r, 1), Some("a".into()));
        assert_eq!(grapheme_before(&r, 3), Some("c".into()));
    }

    #[test]
    fn word_boundaries() {
        let r = rope("hello world");
        // Forward: skip whitespace, then consume word
        assert_eq!(next_word_boundary(&r, 0), 5); // "hello"
        assert_eq!(next_word_boundary(&r, 5), 11); // " world"

        // Backward: consume word, then consume whitespace
        assert_eq!(prev_word_boundary(&r, 11), 5); // "world " → back over "world" + space
        assert_eq!(prev_word_boundary(&r, 5), 0); // back over "hello"
    }

    #[test]
    fn word_boundaries_underscore() {
        let r = rope("foo_bar baz");
        assert_eq!(next_word_boundary(&r, 0), 7); // "foo_bar"
        assert_eq!(next_word_boundary(&r, 7), 11); // " baz"
        assert_eq!(prev_word_boundary(&r, 11), 7); // "baz" + " "
        assert_eq!(prev_word_boundary(&r, 7), 0); // "foo_bar"
    }

    #[test]
    fn word_boundaries_punctuation_grouping() {
        let r = rope("is....full");
        assert_eq!(next_word_boundary(&r, 0), 2); // "is"
        assert_eq!(next_word_boundary(&r, 2), 6); // "...."
        assert_eq!(next_word_boundary(&r, 6), 10); // "full"

        assert_eq!(prev_word_boundary(&r, 10), 6); // "full"
        assert_eq!(prev_word_boundary(&r, 6), 2); // "...."
        assert_eq!(prev_word_boundary(&r, 2), 0); // "is"
    }

    #[test]
    fn word_boundaries_mixed_punct_and_ident() {
        let r = rope("other-symbols");
        assert_eq!(next_word_boundary(&r, 0), 5); // "other"
        assert_eq!(next_word_boundary(&r, 5), 6); // "-"
        assert_eq!(next_word_boundary(&r, 6), 13); // "symbols"

        assert_eq!(prev_word_boundary(&r, 13), 6); // "symbols"
        assert_eq!(prev_word_boundary(&r, 6), 5); // "-"
        assert_eq!(prev_word_boundary(&r, 5), 0); // "other"
    }

    #[test]
    fn word_boundaries_multiple_spaces() {
        let r = rope("this      text");
        assert_eq!(next_word_boundary(&r, 0), 4); // "this"
        assert_eq!(next_word_boundary(&r, 4), 14); // "      text"

        assert_eq!(prev_word_boundary(&r, 14), 4); // "text" + "      "
        assert_eq!(prev_word_boundary(&r, 4), 0); // "this"
    }

    #[test]
    fn line_start_end() {
        let r = rope("abc\ndef\nghi");
        // "abc\ndef\nghi"
        // chars: a(0) b(1) c(2) \n(3) d(4) e(5) f(6) \n(7) g(8) h(9) i(10)

        assert_eq!(line_start(&r, 0), 0);
        assert_eq!(line_start(&r, 2), 0);
        assert_eq!(line_start(&r, 4), 4);
        assert_eq!(line_start(&r, 8), 8);

        // line_end returns the char index after last content char (before newline)
        assert_eq!(line_end(&r, 0), 3); // "abc" → end at 3
        assert_eq!(line_end(&r, 4), 7); // "def" → end at 7 (exclusive, before \n? No...)
        // Actually let me trace through:
        // line_idx = char_to_line(4) = 1
        // line = rope.line(1) = "def\n" (4 chars)
        // line_char_start = line_to_char(1) = 4
        // line_len = 4
        // end = 4 + 4 = 8
        // last_char = rope.char(7) = '\n'
        // end = 8 - 1 = 7
        // So line_end returns 7. In the old code it returned 7 too (byte offset).
        // For ASCII, byte offset == char index, so this matches.
        assert_eq!(line_end(&r, 8), 11); // "ghi" → end at 11
    }

    #[test]
    fn line_up_down() {
        let r = rope("abc\ndef\nghi");
        let (pos, col) = line_up(&r, 4, 0).unwrap();
        assert_eq!(pos, 0);
        assert_eq!(col, 0);

        assert!(line_up(&r, 0, 0).is_none());

        let (pos, col) = line_down(&r, 0, 0).unwrap();
        assert_eq!(pos, 4);
        assert_eq!(col, 0);

        assert!(line_down(&r, 8, 0).is_none());
    }

    #[test]
    fn line_up_preserves_column() {
        let r = rope("abcde\nfg\nhijkl");
        // chars: a(0)b(1)c(2)d(3)e(4)\n(5) f(6)g(7)\n(8) h(9)i(10)j(11)k(12)l(13)
        // From "hijkl" char_pos=13 (col=4), go up to "fg" — clamp to col 2
        let (pos, col) = line_up(&r, 13, 4).unwrap();
        // prev_line_start = line_to_char(1) = 6
        // prev_line_end = line_end(rope, 6) = ?
        // line(1) = "fg\n" (3 chars), end = 6+3=9, char(8)='\n', end=8
        // prev_line_len = 8-6 = 2
        // col = min(4,2) = 2
        // pos = 6+2 = 8
        assert_eq!(pos, 8);
        assert_eq!(col, 2);
    }

    #[test]
    fn current_line_text_extraction() {
        let r = rope("abc\ndef\nghi");
        assert_eq!(current_line_text(&r, 0), "abc");
        assert_eq!(current_line_text(&r, 4), "def");
        assert_eq!(current_line_text(&r, 8), "ghi");
    }

    #[test]
    fn word_at_position() {
        let r = rope("hello world");
        assert_eq!(word_at(&r, 0), "hello");
        assert_eq!(word_at(&r, 3), "hello");
        assert_eq!(word_at(&r, 6), "world");
    }

    #[test]
    fn word_at_underscore() {
        let r = rope("foo_bar baz");
        assert_eq!(word_at(&r, 0), "foo_bar");
        assert_eq!(word_at(&r, 4), "foo_bar"); // on the '_'
        assert_eq!(word_at(&r, 5), "foo_bar"); // on 'b' in bar (part of foo_bar)
    }

    // ── Windows-style word start navigation ──

    #[test]
    fn prev_word_start_basic() {
        let r = rope("hello world");
        assert_eq!(prev_word_start(&r, 11), 6); // end → start of "world"
        assert_eq!(prev_word_start(&r, 8), 6);  // mid "world" → start of "world"
        assert_eq!(prev_word_start(&r, 6), 0);  // start of "world" → start of "hello"
        assert_eq!(prev_word_start(&r, 5), 0);  // on space → start of "hello"
        assert_eq!(prev_word_start(&r, 0), 0);  // already at start
    }

    #[test]
    fn next_word_start_basic() {
        let r = rope("hello world");
        assert_eq!(next_word_start(&r, 0), 6);  // start of "hello" → start of "world"
        assert_eq!(next_word_start(&r, 3), 6);  // mid "hello" → start of "world"
        assert_eq!(next_word_start(&r, 6), 11); // start of "world" → end (no next word)
        assert_eq!(next_word_start(&r, 11), 11); // already at end
    }

    #[test]
    fn prev_word_start_multiple_spaces() {
        let r = rope("foo   bar");
        assert_eq!(prev_word_start(&r, 9), 6); // end → start of "bar"
        assert_eq!(prev_word_start(&r, 6), 0); // start of "bar" → start of "foo"
        assert_eq!(prev_word_start(&r, 5), 0); // mid spaces → start of "foo"
    }

    #[test]
    fn next_word_start_multiple_spaces() {
        let r = rope("foo   bar");
        assert_eq!(next_word_start(&r, 0), 6); // start of "foo" → start of "bar"
        assert_eq!(next_word_start(&r, 3), 6); // end of "foo" (on space) → start of "bar"
    }

    #[test]
    fn prev_word_start_punctuation() {
        let r = rope("is....full");
        assert_eq!(prev_word_start(&r, 10), 6); // end → start of "full"
        assert_eq!(prev_word_start(&r, 6), 2);  // start of "full" → start of "...."
        assert_eq!(prev_word_start(&r, 2), 0);  // start of "...." → start of "is"
    }

    #[test]
    fn next_word_start_punctuation() {
        let r = rope("is....full");
        assert_eq!(next_word_start(&r, 0), 2);  // start of "is" → start of "...."
        assert_eq!(next_word_start(&r, 2), 6);  // start of "...." → start of "full"
        assert_eq!(next_word_start(&r, 6), 10); // start of "full" → end
    }

    #[test]
    fn word_start_empty() {
        let r = rope("");
        assert_eq!(prev_word_start(&r, 0), 0);
        assert_eq!(next_word_start(&r, 0), 0);
    }

    // ── Word-extent (Notepad-style) tests ──

    #[test]
    fn next_word_extent_basic() {
        let r = rope("hello world");
        assert_eq!(next_word_extent(&r, 0), 6);  // "hello " → start of "world"
        assert_eq!(next_word_extent(&r, 6), 11); // "world" → end
    }

    #[test]
    fn prev_word_extent_basic() {
        let r = rope("hello world");
        assert_eq!(prev_word_extent(&r, 11), 6); // end → start of "world"
        assert_eq!(prev_word_extent(&r, 6), 0);  // " hello" → start
    }

    #[test]
    fn word_extent_comma_space() {
        // "Hello, world!" — the motivating example
        let r = rope("Hello, world!");
        assert_eq!(next_word_extent(&r, 0), 7);  // "Hello, " → start of "world"
        assert_eq!(next_word_extent(&r, 7), 13); // "world!" → end
        assert_eq!(prev_word_extent(&r, 13), 7); // end → start of "world"
        assert_eq!(prev_word_extent(&r, 7), 0);  // ", Hello" → start
    }

    #[test]
    fn word_extent_punctuation() {
        // Notepad-style: punctuation grouped with preceding word
        let r = rope("is....full");
        assert_eq!(next_word_extent(&r, 0), 6);  // "is...." → start of "full"
        assert_eq!(next_word_extent(&r, 6), 10); // "full" → end
        assert_eq!(prev_word_extent(&r, 10), 6); // end → start of "full"
        assert_eq!(prev_word_extent(&r, 6), 0);  // "....is" → start
    }

    #[test]
    fn word_extent_empty() {
        let r = rope("");
        assert_eq!(prev_word_extent(&r, 0), 0);
        assert_eq!(next_word_extent(&r, 0), 0);
    }

    #[test]
    fn empty_rope() {
        let r = rope("");
        assert_eq!(prev_grapheme(&r, 0), None);
        assert_eq!(next_grapheme(&r, 0), None);
        assert_eq!(grapheme_at(&r, 0), None);
        assert_eq!(line_start(&r, 0), 0);
        assert_eq!(line_end(&r, 0), 0);
    }
}

#[cfg(test)]
mod word_boundary_proptests {
    use super::*;
    use proptest::prelude::*;

    proptest! {
        /// Forward word boundary always makes progress unless only trailing
        /// whitespace remains.
        #[test]
        fn next_word_boundary_makes_progress(
            text in "[a-zA-Z0-9_.,:;!? ]{1,100}",
        ) {
            let r = Rope::from_str(&text);
            let char_len = r.len_chars();
            let mut pos = 0;
            let mut steps = 0;
            while pos < char_len {
                let next = next_word_boundary(&r, pos);
                if next == pos {
                    // Only trailing whitespace remains
                    let remaining: String = r.slice(pos..char_len).to_string();
                    prop_assert!(
                        remaining.chars().all(|c| c.is_whitespace()),
                        "next_word_boundary({}) stalled but non-ws remains: {:?}",
                        pos, remaining
                    );
                    break;
                }
                prop_assert!(
                    next > pos,
                    "next_word_boundary({}) did not advance (returned {}), text={:?}",
                    pos, next, text
                );
                pos = next;
                steps += 1;
                prop_assert!(steps <= char_len, "infinite loop detected");
            }
        }

        /// Backward word boundary always makes progress.
        #[test]
        fn prev_word_boundary_makes_progress(
            text in "[a-zA-Z0-9_.,:;!? ]{1,100}",
        ) {
            let r = Rope::from_str(&text);
            let char_len = r.len_chars();
            let mut pos = char_len;
            let mut steps = 0;
            while pos > 0 {
                let prev = prev_word_boundary(&r, pos);
                prop_assert!(
                    prev < pos,
                    "prev_word_boundary({}) did not retreat (returned {}), text={:?}",
                    pos, prev, text
                );
                pos = prev;
                steps += 1;
                prop_assert!(steps <= char_len, "infinite loop detected");
            }
        }

        /// Forward/backward cover the full text.
        #[test]
        fn word_boundaries_cover_full_text(
            text in "[a-zA-Z0-9_.,:;!? ]{1,100}",
        ) {
            let r = Rope::from_str(&text);
            let char_len = r.len_chars();

            let mut pos = 0;
            let mut steps = 0;
            while pos < char_len {
                let next = next_word_boundary(&r, pos);
                if next == pos { break; }
                pos = next;
                steps += 1;
                prop_assert!(steps <= char_len, "forward infinite loop");
            }

            pos = char_len;
            steps = 0;
            while pos > 0 {
                let prev = prev_word_boundary(&r, pos);
                prop_assert!(prev < pos);
                pos = prev;
                steps += 1;
                prop_assert!(steps <= char_len, "backward infinite loop");
            }
            prop_assert_eq!(pos, 0);
        }

        /// Word boundaries always land on valid char boundaries.
        #[test]
        fn word_boundaries_on_char_boundaries(
            text in ".{1,100}",
        ) {
            let r = Rope::from_str(&text);
            let char_len = r.len_chars();

            let mut pos = 0;
            while pos < char_len {
                let next = next_word_boundary(&r, pos);
                if next == pos { break; }
                prop_assert!(
                    next <= char_len,
                    "next_word_boundary({}) = {} exceeds char_len {} in {:?}",
                    pos, next, char_len, text
                );
                pos = next;
            }

            pos = char_len;
            while pos > 0 {
                let prev = prev_word_boundary(&r, pos);
                prop_assert!(
                    prev <= char_len,
                    "prev_word_boundary({}) = {} exceeds char_len {} in {:?}",
                    pos, prev, char_len, text
                );
                pos = prev;
            }
        }
    }
}
