using System.Globalization;
using System.Text;

namespace Srui.Core;

/// <summary>Pure text navigation algorithms over <see cref="Rope"/> —
/// grapheme-aware. All positions are UTF-16 code-unit indices. Grapheme
/// boundaries come from .NET's text-element segmentation; word boundaries
/// are character-class scans (word = letter/digit/underscore, punctuation
/// = non-word non-whitespace), classified per rune so surrogate-pair
/// characters behave like their scalar values.</summary>
internal static class TextNav
{
    // ── Rune helpers ──

    /// <summary>The rune starting at <paramref name="pos"/> and its width
    /// in code units. Lone surrogates read as U+FFFD, width 1.</summary>
    private static (System.Text.Rune Rune, int Width) RuneAt(Rope rope, int pos)
    {
        var c = rope.CharAt(pos);
        if (char.IsHighSurrogate(c) && pos + 1 < rope.Length)
        {
            var c2 = rope.CharAt(pos + 1);
            if (char.IsLowSurrogate(c2))
                return (new Rune(c, c2), 2);
        }
        return (char.IsSurrogate(c) ? Rune.ReplacementChar : new Rune(c), 1);
    }

    /// <summary>The rune ending at <paramref name="pos"/> and its width.</summary>
    private static (System.Text.Rune Rune, int Width) RuneBefore(Rope rope, int pos)
    {
        var c = rope.CharAt(pos - 1);
        if (char.IsLowSurrogate(c) && pos - 2 >= 0)
        {
            var c2 = rope.CharAt(pos - 2);
            if (char.IsHighSurrogate(c2))
                return (new Rune(c2, c), 2);
        }
        return (char.IsSurrogate(c) ? Rune.ReplacementChar : new Rune(c), 1);
    }

    // ── Character class helpers ──

    /// <summary>Identifier character: letter, number, or underscore
    /// (mirrors Rust's char::is_alphanumeric plus '_').</summary>
    private static bool IsWordChar(Rune c) =>
        Rune.IsLetter(c) || Rune.IsNumber(c) || c.Value == '_';

    /// <summary>Punctuation: non-word, non-whitespace.</summary>
    private static bool IsPunct(Rune c) => !IsWordChar(c) && !Rune.IsWhiteSpace(c);

    // ── Grapheme helpers ──

    /// <summary>The previous grapheme cluster boundary, or null at the
    /// text start. Scans a window anchored at a safe boundary (text start
    /// or just after a newline) and walks text elements forward.</summary>
    public static int? PrevGrapheme(Rope rope, int pos)
    {
        if (pos <= 0)
            return null;
        pos = Math.Min(pos, rope.Length);

        var window = 32;
        while (true)
        {
            var start = SafeAnchorBefore(rope, pos, window);
            var text = rope.Substring(start, pos);
            var i = 0;
            var last = 0;
            while (i < text.Length)
            {
                last = i;
                var len = StringInfo.GetNextTextElementLength(text.AsSpan(i));
                if (len <= 0)
                    break;
                i += len;
            }
            // If the whole window is one element and the anchor is not a
            // certain boundary, widen and retry.
            if (last == 0 && start > 0 && window < 4096)
            {
                window *= 4;
                continue;
            }
            return start + last;
        }
    }

    /// <summary>A window start strictly before pos that is a safe
    /// segmentation anchor: the text start or a position right after a
    /// '\n'. The newline search excludes pos - 1 so a newline cluster
    /// ending at pos stays inside the window (it may be "\r\n").</summary>
    private static int SafeAnchorBefore(Rope rope, int pos, int window)
    {
        var start = Math.Max(0, pos - window);
        // Snap to just after a newline when one is inside the window —
        // '\n' terminates every cluster, so that position is a boundary.
        var newline = rope.LastNewlineBefore(pos - 1);
        if (newline >= 0 && newline + 1 >= start)
            return newline + 1;
        // Otherwise avoid splitting a surrogate pair at the window edge.
        if (start > 0 && char.IsLowSurrogate(rope.CharAt(start)))
            start--;
        return start;
    }

    /// <summary>The next grapheme cluster boundary, or null at the text
    /// end.</summary>
    public static int? NextGrapheme(Rope rope, int pos)
    {
        var length = rope.Length;
        if (pos >= length)
            return null;
        var window = 32;
        while (true)
        {
            var end = Math.Min(pos + window, length);
            var text = rope.Substring(pos, end);
            var len = StringInfo.GetNextTextElementLength(text.AsSpan());
            if (len <= 0)
                return null;
            // A cluster filling the whole window may continue past it.
            if (len == text.Length && end < length && window < 4096)
            {
                window *= 4;
                continue;
            }
            return pos + len;
        }
    }

    /// <summary>The grapheme cluster starting at <paramref name="pos"/>.</summary>
    public static string? GraphemeAt(Rope rope, int pos)
    {
        if (pos >= rope.Length)
            return null;
        return NextGrapheme(rope, pos) is int next ? rope.Substring(pos, next) : null;
    }

    /// <summary>The grapheme cluster ending at <paramref name="pos"/>.</summary>
    public static string? GraphemeBefore(Rope rope, int pos)
    {
        if (pos <= 0)
            return null;
        return PrevGrapheme(rope, pos) is int prev ? rope.Substring(prev, pos) : null;
    }

    // ── Word boundary navigation (code-editor style) ──

    /// <summary>Move to the previous word boundary: consume one run of
    /// identifier or punctuation characters backward, then any whitespace
    /// before it.</summary>
    public static int PrevWordBoundary(Rope rope, int pos)
    {
        if (pos <= 0)
            return 0;
        pos = Math.Min(pos, rope.Length);

        var (first, firstWidth) = RuneBefore(rope, pos);
        var p = pos - firstWidth;

        if (IsWordChar(first))
        {
            while (p > 0)
            {
                var (c, w) = RuneBefore(rope, p);
                if (IsWordChar(c))
                {
                    p -= w;
                }
                else if (Rune.IsWhiteSpace(c))
                {
                    p -= w;
                    p = ConsumeWhitespaceBackward(rope, p);
                    break;
                }
                else
                {
                    break;
                }
            }
        }
        else if (IsPunct(first))
        {
            while (p > 0)
            {
                var (c, w) = RuneBefore(rope, p);
                if (IsPunct(c))
                {
                    p -= w;
                }
                else if (Rune.IsWhiteSpace(c))
                {
                    p -= w;
                    p = ConsumeWhitespaceBackward(rope, p);
                    break;
                }
                else
                {
                    break;
                }
            }
        }
        else
        {
            // First char was whitespace.
            while (p > 0)
            {
                var (c, w) = RuneBefore(rope, p);
                if (Rune.IsWhiteSpace(c))
                {
                    p -= w;
                }
                else if (IsWordChar(c))
                {
                    p -= w;
                    while (p > 0)
                    {
                        var (c2, w2) = RuneBefore(rope, p);
                        if (!IsWordChar(c2))
                            break;
                        p -= w2;
                    }
                    break;
                }
                else
                {
                    p -= w;
                    while (p > 0)
                    {
                        var (c2, w2) = RuneBefore(rope, p);
                        if (!IsPunct(c2))
                            break;
                        p -= w2;
                    }
                    break;
                }
            }
        }

        return p;
    }

    private static int ConsumeWhitespaceBackward(Rope rope, int p)
    {
        while (p > 0)
        {
            var (c, w) = RuneBefore(rope, p);
            if (!Rune.IsWhiteSpace(c))
                break;
            p -= w;
        }
        return p;
    }

    /// <summary>Move to the next word boundary: consume any whitespace at
    /// the cursor, then one run of identifier or punctuation characters.</summary>
    public static int NextWordBoundary(Rope rope, int pos)
    {
        var length = rope.Length;
        if (pos >= length)
            return length;

        var p = pos;
        // Phase 1: skip whitespace.
        System.Text.Rune firstNonWs;
        while (true)
        {
            if (p >= length)
                return pos;
            var (c, w) = RuneAt(rope, p);
            if (Rune.IsWhiteSpace(c))
            {
                p += w;
            }
            else
            {
                firstNonWs = c;
                p += w;
                break;
            }
        }

        // Phase 2: consume one run of word or punctuation chars.
        if (IsWordChar(firstNonWs))
        {
            while (p < length)
            {
                var (c, w) = RuneAt(rope, p);
                if (!IsWordChar(c))
                    break;
                p += w;
            }
        }
        else
        {
            while (p < length)
            {
                var (c, w) = RuneAt(rope, p);
                if (!IsPunct(c))
                    break;
                p += w;
            }
        }

        return p;
    }

    // ── Word-start navigation (Windows Ctrl+Arrow style) ──

    /// <summary>Move left to the start of the current word (if not already
    /// there), or the start of the previous word.</summary>
    public static int PrevWordStart(Rope rope, int pos)
    {
        if (pos <= 0)
            return 0;
        var p = Math.Min(pos, rope.Length);

        // Phase 1: skip whitespace immediately before the cursor.
        while (p > 0)
        {
            var (c, w) = RuneBefore(rope, p);
            if (!Rune.IsWhiteSpace(c))
                break;
            p -= w;
        }
        if (p == 0)
            return 0;

        // Phase 2: skip the word (or punctuation run) we're at the end of.
        var (first, firstWidth) = RuneBefore(rope, p);
        if (IsWordChar(first))
        {
            p -= firstWidth;
            while (p > 0)
            {
                var (c, w) = RuneBefore(rope, p);
                if (!IsWordChar(c))
                    break;
                p -= w;
            }
        }
        else if (IsPunct(first))
        {
            p -= firstWidth;
            while (p > 0)
            {
                var (c, w) = RuneBefore(rope, p);
                if (!IsPunct(c))
                    break;
                p -= w;
            }
        }

        return p;
    }

    /// <summary>Move right to the start of the next word: skip the
    /// remainder of the current word/punctuation run, then any whitespace.
    /// Lands at the text end when no next word exists.</summary>
    public static int NextWordStart(Rope rope, int pos)
    {
        var length = rope.Length;
        if (pos >= length)
            return length;

        var p = pos;
        // Phase 1: skip the current word/punctuation run (or one
        // whitespace char).
        var (first, firstWidth) = RuneAt(rope, p);
        p += firstWidth;
        if (IsWordChar(first))
        {
            while (p < length)
            {
                var (c, w) = RuneAt(rope, p);
                if (!IsWordChar(c))
                    break;
                p += w;
            }
        }
        else if (IsPunct(first))
        {
            while (p < length)
            {
                var (c, w) = RuneAt(rope, p);
                if (!IsPunct(c))
                    break;
                p += w;
            }
        }

        // Phase 2: skip whitespace to reach the next word start.
        while (p < length)
        {
            var (c, w) = RuneAt(rope, p);
            if (!Rune.IsWhiteSpace(c))
                break;
            p += w;
        }

        return p;
    }

    // ── Word-extent navigation (Notepad style) ──
    // A word "extent" = word + all trailing non-word chars (punctuation +
    // whitespace). Used for Ctrl+Shift+Arrow selection and Ctrl+Backspace/Delete.

    /// <summary>Move left to the start of the previous word extent: skip
    /// backward over all non-word chars, then over the word itself.</summary>
    public static int PrevWordExtent(Rope rope, int pos)
    {
        if (pos <= 0)
            return 0;
        var p = Math.Min(pos, rope.Length);

        // Phase 1: skip backward over non-word chars.
        while (p > 0)
        {
            var (c, w) = RuneBefore(rope, p);
            if (IsWordChar(c))
                break;
            p -= w;
        }
        if (p == 0)
            return 0;

        // Phase 2: skip backward over word chars.
        while (p > 0)
        {
            var (c, w) = RuneBefore(rope, p);
            if (!IsWordChar(c))
                break;
            p -= w;
        }

        return p;
    }

    /// <summary>Move right to the start of the next word extent: skip the
    /// rest of the current word (when on one), then all trailing non-word
    /// chars.</summary>
    public static int NextWordExtent(Rope rope, int pos)
    {
        var length = rope.Length;
        if (pos >= length)
            return length;

        var p = pos;
        // Phase 1: if on a word char, skip the rest of the word.
        var (first, firstWidth) = RuneAt(rope, p);
        p += firstWidth;
        if (IsWordChar(first))
        {
            while (p < length)
            {
                var (c, w) = RuneAt(rope, p);
                if (!IsWordChar(c))
                    break;
                p += w;
            }
        }

        // Phase 2: skip all non-word chars.
        while (p < length)
        {
            var (c, w) = RuneAt(rope, p);
            if (IsWordChar(c))
                break;
            p += w;
        }

        return p;
    }

    // ── Line navigation ──

    /// <summary>The index of the start of the line containing
    /// <paramref name="pos"/>.</summary>
    public static int LineStart(Rope rope, int pos)
    {
        if (pos <= 0 || rope.Length == 0)
            return 0;
        var clamped = Math.Min(pos, rope.Length);
        return rope.LastNewlineBefore(clamped) + 1;
    }

    /// <summary>The index of the end of the line containing
    /// <paramref name="pos"/> (before its newline / CRLF, or the text end).</summary>
    public static int LineEnd(Rope rope, int pos)
    {
        var length = rope.Length;
        if (pos >= length)
            return length;
        var newline = rope.IndexOfNewline(pos);
        var end = newline >= 0 ? newline : length;
        var start = LineStart(rope, pos);
        if (newline >= 0 && end > start && rope.CharAt(end - 1) == '\r')
            end--;
        return end;
    }

    /// <summary>Move up one line preserving the preferred column. Returns
    /// (new position, new column), or null on the first line.</summary>
    public static (int Pos, int Column)? LineUp(Rope rope, int pos, int preferredColumn)
    {
        var clamped = Math.Min(pos, rope.Length);
        var currentStart = LineStart(rope, clamped);
        if (currentStart == 0)
            return null;
        var prevStart = LineStart(rope, currentStart - 1);
        var prevEnd = LineEnd(rope, prevStart);
        var prevLen = Math.Max(prevEnd - prevStart, 0);
        var column = Math.Min(preferredColumn, prevLen);
        return (prevStart + column, column);
    }

    /// <summary>Move down one line preserving the preferred column.
    /// Returns null on the last line.</summary>
    public static (int Pos, int Column)? LineDown(Rope rope, int pos, int preferredColumn)
    {
        var clamped = Math.Min(pos, rope.Length);
        var newline = rope.IndexOfNewline(clamped);
        if (newline < 0)
            return null;
        var nextStart = newline + 1;
        var nextEnd = LineEnd(rope, nextStart);
        var nextLen = Math.Max(nextEnd - nextStart, 0);
        var column = Math.Min(preferredColumn, nextLen);
        return (nextStart + column, column);
    }

    /// <summary>The text of the line containing <paramref name="pos"/>
    /// (without its trailing newline).</summary>
    public static string CurrentLineText(Rope rope, int pos)
    {
        var start = LineStart(rope, pos);
        var end = LineEnd(rope, pos);
        return start >= end ? "" : rope.Substring(start, end);
    }

    // ── Line/column conversion ──
    // Lines and columns are 0-based; a column is the UTF-16 code-unit
    // offset from the line start, matching every other position on the
    // surface.

    /// <summary>Number of lines: newlines + 1.</summary>
    public static int LineCount(Rope rope) => rope.NewlinesBefore(rope.Length) + 1;

    /// <summary>The line and column of <paramref name="pos"/> (clamped).</summary>
    public static (int Line, int Column) LineColumnAt(Rope rope, int pos)
    {
        pos = Math.Clamp(pos, 0, rope.Length);
        return (rope.NewlinesBefore(pos), pos - LineStart(rope, pos));
    }

    /// <summary>The position of (line, column), clamped: the line to the
    /// text's last line, the column to the addressed line's end (before
    /// its newline / CRLF).</summary>
    public static int PositionOfLineColumn(Rope rope, int line, int column)
    {
        var start = 0;
        for (var i = 0; i < line; i++)
        {
            var newline = rope.IndexOfNewline(start);
            if (newline < 0)
                break;
            start = newline + 1;
        }
        var end = LineEnd(rope, start);
        return start + Math.Clamp(column, 0, end - start);
    }

    /// <summary>Skip whitespace forward, returning the index of the first
    /// non-whitespace character.</summary>
    public static int SkipWhitespaceForward(Rope rope, int pos)
    {
        var length = rope.Length;
        var p = pos;
        while (p < length)
        {
            var (c, w) = RuneAt(rope, p);
            if (!Rune.IsWhiteSpace(c))
                break;
            p += w;
        }
        return p;
    }

    /// <summary>The word surrounding or starting at <paramref name="pos"/>.
    /// A non-word character yields just that character.</summary>
    public static string WordAt(Rope rope, int pos)
    {
        var length = rope.Length;
        if (pos >= length)
            return "";

        var (cursorRune, cursorWidth) = RuneAt(rope, pos);
        if (!IsWordChar(cursorRune))
            return cursorRune.ToString();

        // Scan backward to the start of the word.
        var start = pos;
        while (start > 0)
        {
            var (c, w) = RuneBefore(rope, start);
            if (!IsWordChar(c))
                break;
            start -= w;
        }

        // Scan forward to the end of the word.
        var end = pos + cursorWidth;
        while (end < length)
        {
            var (c, w) = RuneAt(rope, end);
            if (!IsWordChar(c))
                break;
            end += w;
        }

        return rope.Substring(start, end);
    }
}
