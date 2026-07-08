using System.Text;

namespace Srui.Core;

/// <summary>EditBox input handling — single/multi-line text editing with
/// full cursor navigation, selection, clipboard, and accessible event
/// emission. <see cref="Handle"/> is a pure function over
/// <see cref="EditorState"/>; <see cref="EditBoxBehavior"/> wraps it.</summary>
internal static class EditBoxCore
{
    /// <summary>Result of <see cref="Handle"/>: events to dispatch, plus
    /// whether the input was consumed (for navigation fallthrough) and
    /// whether text was mutated.</summary>
    public sealed class Result
    {
        public readonly List<AccessibilityEvent> Events = new();
        public bool Consumed;
        public bool Changed;

        /// <summary>Not handled — let framework navigation take it.</summary>
        public static Result Ignored() => new();

        public static Result JustConsumed() => new() { Consumed = true };

        public static Result Announce(string text)
        {
            var result = JustConsumed();
            result.Events.Add(new AccessibilityEvent.Announce(text));
            return result;
        }
    }

    private enum NavKind { Char, Word, LineEdge, TextEdge, LineUp, LineDown }

    private static NavGranularity ToGranularity(NavKind kind) => kind switch
    {
        NavKind.Char => NavGranularity.Char,
        NavKind.Word => NavGranularity.Word,
        NavKind.LineEdge => NavGranularity.LineEdge,
        NavKind.TextEdge => NavGranularity.TextEdge,
        NavKind.LineUp => NavGranularity.LineUp,
        _ => NavGranularity.LineDown,
    };

    /// <summary>Character to speak at the cursor. Falls back to the
    /// character before the cursor when the cursor is past the text end.</summary>
    private static string CursorSpeakChar(EditorState editor, bool skipNewline)
    {
        var at = TextNav.GraphemeAt(editor.Rope, editor.Cursor);
        if (at is string g && (!skipNewline || (g != "\n" && g != "\r")))
            return SpeechRenderer.SpeakChar(g);
        if (editor.Cursor > 0)
        {
            return TextNav.GraphemeBefore(editor.Rope, editor.Cursor) is string before
                ? SpeechRenderer.SpeakChar(before)
                : "blank";
        }
        return "blank";
    }

    /// <summary>Raw grapheme at the cursor (no speech expansion) for the
    /// structural TextNav payload.</summary>
    private static string RawGraphemeAtCursor(EditorState editor) =>
        TextNav.GraphemeAt(editor.Rope, editor.Cursor)
        ?? TextNav.GraphemeBefore(editor.Rope, editor.Cursor)
        ?? "";

    /// <summary>Speak a WordAt result — a single non-alphanumeric rune
    /// goes through character expansion.</summary>
    private static string SpeakWord(string word)
    {
        if (word.Length != 0)
        {
            var rune = Rune.GetRuneAt(word, 0);
            if (rune.Utf16SequenceLength == word.Length
                && !(Rune.IsLetter(rune) || Rune.IsNumber(rune)))
                return SpeechRenderer.SpeakChar(word);
        }
        return word;
    }

    /// <summary>The word at the cursor for speech, skipping whitespace to
    /// find the nearest word.</summary>
    private static string WordContext(EditorState editor)
    {
        var pos = TextNav.SkipWhitespaceForward(editor.Rope, editor.Cursor);
        var word = TextNav.WordAt(editor.Rope, pos);
        if (word.Length == 0 && editor.Cursor > 0)
        {
            if (TextNav.GraphemeBefore(editor.Rope, editor.Cursor) is "\n" or "\r\n")
                return "new line";
            var prev = TextNav.PrevWordBoundary(editor.Rope, editor.Cursor);
            var prevPos = TextNav.SkipWhitespaceForward(editor.Rope, prev);
            return SpeakWord(TextNav.WordAt(editor.Rope, prevPos));
        }
        return SpeakWord(word);
    }

    /// <summary>Speech context for the cursor position at a navigation
    /// granularity.</summary>
    private static string NavContext(EditorState editor, NavKind kind)
    {
        switch (kind)
        {
            case NavKind.Char:
                return CursorSpeakChar(editor, false);
            case NavKind.Word:
                return WordContext(editor);
            case NavKind.LineEdge or NavKind.TextEdge:
                return CursorSpeakChar(editor, true);
            default:
            {
                var text = TextNav.CurrentLineText(editor.Rope, editor.Cursor);
                return text.Length == 0 ? "blank" : text;
            }
        }
    }

    /// <summary>Word-echo separator: whitespace or ASCII punctuation.</summary>
    private static bool IsWordSeparator(char c) =>
        char.IsWhiteSpace(c) || (c is >= '!' and <= '~' && !char.IsAsciiLetterOrDigit(c));

    /// <summary>Should a separator typed at the cursor trigger a word
    /// echo? Only when it is the first separator in a run — the char
    /// immediately before it is a non-separator. Without this guard,
    /// "hello..." would re-announce "hello" for every trailing dot.</summary>
    private static bool FirstSeparatorInRun(Rope rope, int cursor) =>
        cursor >= 2 && !IsWordSeparator(rope.CharAt(cursor - 2));

    /// <summary>The word just completed before the cursor (after the
    /// separator was typed), or null when there is no word to speak.</summary>
    private static string? CompletedWord(Rope rope, int cursor)
    {
        if (cursor < 2)
            return null;
        // The cursor is right after the separator; the word ends before it.
        var wordEnd = cursor - 1;
        var wordStart = TextNav.PrevWordBoundary(rope, wordEnd);
        if (wordStart >= wordEnd)
            return null;
        var word = rope.Substring(wordStart, wordEnd).Trim();
        return word.Length == 0 ? null : word;
    }

    private static (NavKind Kind, bool Forward)? ClassifyNav(InputKind kind) => kind switch
    {
        InputKind.MoveLeft => (NavKind.Char, false),
        InputKind.MoveRight => (NavKind.Char, true),
        InputKind.MoveWordLeft => (NavKind.Word, false),
        InputKind.MoveWordRight => (NavKind.Word, true),
        InputKind.MoveToLineStart => (NavKind.LineEdge, false),
        InputKind.MoveToLineEnd => (NavKind.LineEdge, true),
        InputKind.MoveToDocStart => (NavKind.TextEdge, false),
        InputKind.MoveToDocEnd => (NavKind.TextEdge, true),
        InputKind.MoveLineUp or InputKind.MoveUp => (NavKind.LineUp, false),
        InputKind.MoveLineDown or InputKind.MoveDown => (NavKind.LineDown, true),
        _ => null,
    };

    private static (NavKind Kind, bool Forward)? ClassifySelect(InputKind kind) => kind switch
    {
        InputKind.SelectLeft => (NavKind.Char, false),
        InputKind.SelectRight => (NavKind.Char, true),
        InputKind.SelectWordLeft => (NavKind.Word, false),
        InputKind.SelectWordRight => (NavKind.Word, true),
        InputKind.SelectToLineStart => (NavKind.LineEdge, false),
        InputKind.SelectToLineEnd => (NavKind.LineEdge, true),
        InputKind.SelectToDocStart => (NavKind.TextEdge, false),
        InputKind.SelectToDocEnd => (NavKind.TextEdge, true),
        InputKind.SelectLineUp => (NavKind.LineUp, false),
        InputKind.SelectLineDown => (NavKind.LineDown, true),
        _ => null,
    };

    private static (bool Backward, bool Word)? ClassifyDelete(InputKind kind) => kind switch
    {
        InputKind.DeleteBackward => (true, false),
        InputKind.DeleteForward => (false, false),
        InputKind.DeleteWordBackward => (true, true),
        InputKind.DeleteWordForward => (false, true),
        _ => null,
    };

    private static void DoNav(EditorState editor, InputKind kind)
    {
        switch (kind)
        {
            case InputKind.MoveLeft: editor.MoveLeft(); break;
            case InputKind.MoveRight: editor.MoveRight(); break;
            case InputKind.MoveWordLeft: editor.MoveWordLeft(); break;
            case InputKind.MoveWordRight: editor.MoveWordRight(); break;
            case InputKind.MoveToLineStart: editor.MoveToLineStart(); break;
            case InputKind.MoveToLineEnd: editor.MoveToLineEnd(); break;
            case InputKind.MoveToDocStart: editor.MoveToDocStart(); break;
            case InputKind.MoveToDocEnd: editor.MoveToDocEnd(); break;
            case InputKind.MoveLineUp or InputKind.MoveUp: editor.MoveLineUp(); break;
            case InputKind.MoveLineDown or InputKind.MoveDown: editor.MoveLineDown(); break;
        }
    }

    private static void DoSelect(EditorState editor, InputKind kind)
    {
        switch (kind)
        {
            case InputKind.SelectLeft: editor.SelectLeft(); break;
            case InputKind.SelectRight: editor.SelectRight(); break;
            case InputKind.SelectWordLeft: editor.SelectWordLeft(); break;
            case InputKind.SelectWordRight: editor.SelectWordRight(); break;
            case InputKind.SelectToLineStart: editor.SelectToLineStart(); break;
            case InputKind.SelectToLineEnd: editor.SelectToLineEnd(); break;
            case InputKind.SelectToDocStart: editor.SelectToDocStart(); break;
            case InputKind.SelectToDocEnd: editor.SelectToDocEnd(); break;
            case InputKind.SelectLineUp: editor.SelectLineUp(); break;
            case InputKind.SelectLineDown: editor.SelectLineDown(); break;
        }
    }

    /// <summary>Handle input for a focused edit box. Reads and writes the
    /// editor state directly; sets Changed on the result when text is
    /// modified.</summary>
    public static Result Handle(NodeId node, in InputEvent input, EditorState editor, IClipboard clipboard)
    {
        var prevCursor = editor.Cursor;
        var prevSelection = editor.Selection;
        var hadSelection = editor.HasSelection;
        var wasEmpty = editor.IsEmpty;

        // ── Navigation (non-shift) ──
        if (ClassifyNav(input.Kind) is (var navKind, var forward))
        {
            DoNav(editor, input.Kind);

            var cursorMoved = editor.Cursor != prevCursor
                || (hadSelection && !editor.HasSelection);

            if (wasEmpty)
                return Result.Announce("No text");

            var result = Result.JustConsumed();
            if (cursorMoved)
            {
                var atEnd = navKind is NavKind.Char or NavKind.TextEdge
                    && editor.Cursor >= editor.Length
                    && prevCursor < editor.Length;
                result.Events.Add(new AccessibilityEvent.TextNav(
                    node,
                    RawGraphemeAtCursor(editor),
                    NavContext(editor, navKind),
                    ToGranularity(navKind),
                    atEnd ? Boundary.Bottom : null));
            }
            else
            {
                result.Events.Add(new AccessibilityEvent.TextNav(
                    node,
                    RawGraphemeAtCursor(editor),
                    NavContext(editor, navKind),
                    ToGranularity(navKind),
                    forward ? Boundary.Bottom : Boundary.Top));
            }
            return result;
        }

        // ── Selection (shift+nav) ──
        if (ClassifySelect(input.Kind) is (var selKind, var selForward))
        {
            var anchor = prevSelection is (var a, _) ? a : prevCursor;

            DoSelect(editor, input.Kind);

            var cursorMoved = editor.Cursor != prevCursor;

            if (wasEmpty)
                return Result.Announce("Nothing to select");

            if (cursorMoved)
            {
                var result = Result.JustConsumed();
                var prevDist = Math.Abs(prevCursor - anchor);
                var newDist = Math.Abs(editor.Cursor - anchor);
                var isUnselecting = newDist < prevDist;

                var selStart = Math.Min(prevCursor, editor.Cursor);
                var selEnd = Math.Max(prevCursor, editor.Cursor);
                var deltaLen = selEnd - selStart;
                if (deltaLen == 0)
                {
                    // A selection step that didn't change content — speak
                    // the cursor char like a regular Char nav.
                    result.Events.Add(new AccessibilityEvent.TextNav(
                        node,
                        RawGraphemeAtCursor(editor),
                        CursorSpeakChar(editor, false),
                        NavGranularity.Char,
                        null));
                    return result;
                }
                var delta = deltaLen > SpeechRenderer.SpeakLimit
                    ? $"{deltaLen} characters"
                    : editor.SliceToString(selStart, selEnd);
                result.Events.Add(new AccessibilityEvent.Selection(
                    node, delta,
                    isUnselecting ? SelectionKind.Unselected : SelectionKind.Selected));
                return result;
            }

            var context = NavContext(editor, selKind);
            var prefix = selForward ? "Already selected to bottom" : "Already selected to top";
            return Result.Announce($"{prefix}, {context}");
        }

        // ── Select All ──
        if (input.Kind == InputKind.SelectAll)
        {
            editor.SelectAll();
            if (wasEmpty)
                return Result.Announce("Nothing to select");
            var length = editor.Length;
            var delta = length > SpeechRenderer.SpeakLimit
                ? $"{length} characters"
                : editor.Text();
            var result = Result.JustConsumed();
            result.Events.Add(new AccessibilityEvent.Selection(node, delta, SelectionKind.All));
            return result;
        }

        // ── Editing: TypeChar ──
        if (input.Kind == InputKind.TypeChar)
        {
            if (editor.ReadOnly)
            {
                // Nothing was inserted, so there is nothing to echo — a
                // read-only editor swallows typing silently.
                return Result.JustConsumed();
            }
            var ch = (char)input.Ch;
            var hadSel = editor.HasSelection;
            editor.InsertChar(ch);

            var result = new Result { Consumed = true, Changed = true };
            if (hadSel)
                result.Events.Add(new AccessibilityEvent.Selection(node, "", SelectionKind.Cleared));

            var lastWord = IsWordSeparator(ch) && FirstSeparatorInRun(editor.Rope, editor.Cursor)
                ? CompletedWord(editor.Rope, editor.Cursor)
                : null;

            result.Events.Add(new AccessibilityEvent.Typing(
                node, ch.ToString(), lastWord, TypingKind.Insert));
            return result;
        }

        // ── Editing: Enter ──
        if (input.Kind == InputKind.Activate)
        {
            // Single-line or read-only: don't consume — let the layer's
            // primary widget handle it.
            if (!editor.Multiline || editor.ReadOnly)
                return Result.Ignored();
            var hadSel = editor.HasSelection;
            editor.InsertNewline();

            var result = new Result { Consumed = true, Changed = true };
            if (hadSel)
                result.Events.Add(new AccessibilityEvent.Selection(node, "", SelectionKind.Cleared));

            var lastWord = FirstSeparatorInRun(editor.Rope, editor.Cursor)
                ? CompletedWord(editor.Rope, editor.Cursor)
                : null;

            result.Events.Add(new AccessibilityEvent.Typing(node, "\n", lastWord, TypingKind.Insert));
            return result;
        }

        // ── Editing: Delete/Backspace ──
        if (ClassifyDelete(input.Kind) is (var backward, var word))
        {
            if (editor.ReadOnly)
                return Result.JustConsumed();
            if (hadSelection)
            {
                editor.DeleteSelectionSilent();
                var cleared = new Result { Consumed = true, Changed = true };
                cleared.Events.Add(new AccessibilityEvent.Selection(node, "", SelectionKind.Cleared));
                return cleared;
            }
            var canDelete = backward ? editor.Cursor > 0 : editor.Cursor < editor.Length;
            if (!canDelete)
                return Result.Announce("Nothing to delete");

            var deleted = (word, backward) switch
            {
                (true, true) => editor.DeleteWordBackward(),
                (true, false) => editor.DeleteWordForward(),
                (false, true) => editor.DeleteBackward(),
                (false, false) => editor.DeleteForward(),
            };
            if (deleted is not string deletedText)
                return Result.JustConsumed();

            var result = new Result { Consumed = true, Changed = true };
            // For char deletes the editor returns the deleted grapheme in
            // spoken form ("dot", "cap A"); it rides in the grapheme field
            // and the renderer's SpeakChar passes multi-char strings through.
            if (word)
                result.Events.Add(new AccessibilityEvent.Typing(node, "", deletedText, TypingKind.DeleteWord));
            else
                result.Events.Add(new AccessibilityEvent.Typing(node, deletedText, null, TypingKind.Delete));
            return result;
        }

        // ── Clipboard ──
        switch (input.Kind)
        {
            case InputKind.Copy:
            {
                if (!hadSelection)
                    return Result.JustConsumed();
                var (clip, _) = editor.Copy();
                if (clip.Length != 0)
                    clipboard.Write(clip);
                var result = Result.JustConsumed();
                result.Events.Add(new AccessibilityEvent.Clipboard(node, ClipboardOp.Copy));
                return result;
            }
            case InputKind.Cut:
            {
                if (editor.ReadOnly || !hadSelection)
                    return Result.JustConsumed();
                var (clip, _) = editor.Cut();
                if (clip.Length != 0)
                    clipboard.Write(clip);
                var result = new Result { Consumed = true, Changed = true };
                result.Events.Add(new AccessibilityEvent.Clipboard(node, ClipboardOp.Cut));
                return result;
            }
            case InputKind.Paste:
            {
                if (editor.ReadOnly)
                    return Result.JustConsumed();
                var text = clipboard.Read() ?? "";
                if (text.Length == 0)
                    return Result.JustConsumed();
                editor.Paste(text);
                var result = new Result { Consumed = true, Changed = true };
                result.Events.Add(new AccessibilityEvent.Clipboard(node, ClipboardOp.Paste));
                return result;
            }
            default:
                return Result.Ignored();
        }
    }

    /// <summary>The label value for an edit box: selection info when
    /// selected, otherwise the current line at the cursor.</summary>
    public static string LabelValue(EditorState editor)
    {
        if (editor.HasSelection)
        {
            var (anchor, cursor) = editor.Selection!.Value;
            var start = Math.Min(anchor, cursor);
            var end = Math.Max(anchor, cursor);
            var length = end - start;
            return length >= SpeechRenderer.SpeakLimit
                ? $"selected {length} characters"
                : $"selected {editor.Rope.Substring(start, end)}";
        }
        if (editor.Length == 0)
            return "blank";
        var line = TextNav.CurrentLineText(editor.Rope, editor.Cursor);
        return line.Length == 0 ? "blank" : line;
    }
}

/// <summary>EditBox — a single- or multi-line text editor with full
/// cursor navigation, selection, clipboard, and typing echo. Enter
/// inserts a newline in multiline editors and falls through to the
/// layer's primary widget in single-line ones.</summary>
internal sealed class EditBoxBehavior : WidgetBehavior
{
    private readonly EditorState _editor;

    public EditBoxBehavior(string text, bool multiline) =>
        _editor = new EditorState(text, multiline);

    public EditorState Editor => _editor;

    public string Text => _editor.Text();

    /// <summary>Replace the content (cursor clamped, selection cleared).</summary>
    public void SetText(string text, WidgetLabel label)
    {
        _editor.SetText(text);
        SyncLabel(label);
    }

    public void SetReadOnly(bool readOnly, WidgetLabel label)
    {
        _editor.ReadOnly = readOnly;
        if (label.Role.Kind == RoleKind.EditBox)
            label.Role = Role.Edit(readOnly, label.Role.Multiline);
        SyncLabel(label);
    }

    /// <summary>Label value mirrors the selection or the current line at
    /// the cursor.</summary>
    public void SyncLabel(WidgetLabel label) => label.Value = EditBoxCore.LabelValue(_editor);

    public override bool HandleInput(in InputEvent input, in WidgetCtx ctx)
    {
        var result = EditBoxCore.Handle(ctx.Node, input, _editor, ctx.Clipboard);
        if (!result.Consumed)
            return false;
        foreach (var ev in result.Events)
            ctx.EmitAccessibility(ev);
        if (result.Changed)
            ctx.EmitWidget(new CoreEvent.Changed(ctx.Node));
        SyncLabel(ctx.Label);
        return true;
    }
}
