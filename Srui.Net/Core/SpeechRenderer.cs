using System.Text;

namespace Srui;

/// <summary>Speech rendering — the reference rendering of accessibility
/// events. Pure functions from structured events to utterance strings.
/// The self-voicing <see cref="SpeechReader"/> uses these; braille and
/// platform readers ignore them and read the structured payloads
/// directly; tests assert against them.</summary>
public static class SpeechRenderer
{
    /// <summary>Maximum characters to announce verbatim for selections;
    /// beyond this we say "N characters selected".</summary>
    public const int SpeakLimit = 500;

    /// <summary>Render an accessibility event to an utterance. Null when
    /// there is nothing sensible to say (readers skip silently).</summary>
    public static string? RenderEvent(AccessibilityEvent ev)
    {
        switch (ev)
        {
            case AccessibilityEvent.Focused(_, var info, var contextLabels, _):
            {
                var announcement = AnnounceFocus(info);
                return contextLabels.Count == 0
                    ? announcement
                    : $"{string.Join(" ", contextLabels)} {announcement}";
            }

            case AccessibilityEvent.Typing(_, var grapheme, var lastWord, var kind):
                // On a word boundary the just-completed word is echoed
                // before the separator: "hello space". For deletes,
                // grapheme is already in spoken form (SpeakChar passes
                // multi-char strings through unchanged).
                return kind switch
                {
                    TypingKind.Insert when lastWord is not null => $"{lastWord} {SpeakChar(grapheme)}",
                    TypingKind.Insert when grapheme.Length != 0 => SpeakChar(grapheme),
                    TypingKind.Insert => null,
                    TypingKind.Delete when grapheme.Length != 0 => SpeakChar(grapheme),
                    TypingKind.Delete => null,
                    TypingKind.DeleteWord => lastWord,
                    _ => null,
                };

            case AccessibilityEvent.TextNav(_, _, var context, _, var boundary):
                return boundary switch
                {
                    Boundary.Top => $"Top, {context}",
                    Boundary.Bottom => $"Bottom, {context}",
                    _ => context,
                };

            case AccessibilityEvent.Selection(_, var delta, var kind):
                return kind switch
                {
                    SelectionKind.Selected or SelectionKind.All => $"{delta} selected",
                    SelectionKind.Unselected => $"{delta} unselected",
                    _ => "Selection removed",
                };

            case AccessibilityEvent.ItemNav(_, var item, var position, var boundary):
            {
                var baseText = position is (var index, var total)
                    ? $"{item} {index + 1} of {total}"
                    : item;
                return boundary switch
                {
                    Boundary.Top => $"top, {baseText}",
                    Boundary.Bottom => $"bottom, {baseText}",
                    _ => baseText,
                };
            }

            // The tab name alone; position and role ride the focus
            // announcement, not the change echo.
            case AccessibilityEvent.TabChange(_, var tabName, _):
                return tabName;

            // Unit directly appended: "50%".
            case AccessibilityEvent.SliderChange(_, var value, var unit):
                return $"{value}{unit}";

            // The same words the focus announcement's value field uses.
            case AccessibilityEvent.Toggle(_, var isChecked):
                return isChecked ? "checked" : "not checked";

            case AccessibilityEvent.Filter(_, _, var firstResult, var resultCount):
                return firstResult is not null
                    ? $"{firstResult} 1 of {resultCount}"
                    : "no results";

            case AccessibilityEvent.Clipboard(_, var op):
                return op switch
                {
                    ClipboardOp.Copy => "Copy",
                    ClipboardOp.Cut => "Cut",
                    _ => "Paste",
                };

            // The delta alone: a renamed widget speaks its new name, a
            // replaced value the new value. Empty text (a cleared name
            // or description) has nothing to speak.
            case AccessibilityEvent.LabelChange(_, _, var newText):
                return newText.Length == 0 ? null : newText;

            case AccessibilityEvent.StateChange(_, var state, var on):
                return state switch
                {
                    // The same word the focus announcement uses.
                    WidgetStates.Disabled => on ? "unavailable" : "available",
                    WidgetStates.Required => on ? "required" : "not required",
                    WidgetStates.Warning => on ? "warning" : "warning cleared",
                    WidgetStates.WithHelp => on ? "with help" : "help removed",
                    _ => null,
                };

            case AccessibilityEvent.EditNoop(_, var kind, var context):
                return kind switch
                {
                    EditNoopKind.NoText => "No text",
                    EditNoopKind.NothingToSelect => "Nothing to select",
                    EditNoopKind.NothingToDelete => "Nothing to delete",
                    EditNoopKind.SelectedToTop => $"Already selected to top, {context}",
                    _ => $"Already selected to bottom, {context}",
                };

            case AccessibilityEvent.Announce(var text, _):
                return text;

            default:
                return null;
        }
    }

    /// <summary>Construct a focus announcement from the golden six, in
    /// NVDA ordering: Name Role Value States Description Shortcut. No
    /// commas between fields; shortcuts spoken as "alt s" not "Alt+S".</summary>
    public static string AnnounceFocus(WidgetInfo info)
    {
        var result = new StringBuilder(64);

        // Every present field separates itself from whatever preceded
        // it, so the utterance never leads with a space regardless of
        // which fields are empty.
        void Append(string field)
        {
            if (field.Length == 0)
                return;
            if (result.Length != 0)
                result.Append(' ');
            result.Append(field);
        }

        // Name (optional — nameless widgets announce as "role value ...").
        Append(info.Name ?? "");

        // Role (empty for role-less widgets — announces without a role word).
        Append(info.Role);

        // Value.
        Append(info.Value);

        // States, dynamic state text first (e.g. "filter ed", "no filter").
        Append(info.StateText);
        if ((info.States & WidgetStates.Disabled) != 0)
            Append("unavailable");
        if ((info.States & WidgetStates.Required) != 0)
            Append("required");
        if ((info.States & WidgetStates.Warning) != 0)
            Append("warning");
        if ((info.States & WidgetStates.WithHelp) != 0)
            Append("with help");

        // Description.
        Append(info.Description);

        // Shortcut — the first one attached, in spoken form ("alt w").
        if (info.Shortcuts.Count != 0)
            Append(info.Shortcuts[0].DisplayName());

        return result.ToString();
    }

    /// <summary>Speak a character with punctuation expansion and case
    /// handling: "a" → "a", "A" → "cap A", " " → "space", "." → "dot",
    /// "\n" → "new line". Multi-char graphemes pass through unchanged.</summary>
    public static string SpeakChar(string ch) =>
        ch.Length == 1 ? SpeakSingleChar(ch[0]) : ch;

    private static string SpeakSingleChar(char c) => c switch
    {
        ' ' => "space",
        '\n' => "new line",
        '\t' => "tab",
        '\r' => "return",

        '.' => "dot",
        ',' => "comma",
        ';' => "semicolon",
        ':' => "colon",
        '!' => "bang",
        '?' => "question mark",
        '\'' => "tick",
        '"' => "quote",
        '(' => "left paren",
        ')' => "right paren",
        '[' => "left bracket",
        ']' => "right bracket",
        '{' => "left brace",
        '}' => "right brace",
        '<' => "less than",
        '>' => "greater than",
        '/' => "slash",
        '\\' => "backslash",
        '|' => "pipe",
        '@' => "at",
        '#' => "number",
        '$' => "dollar",
        '%' => "percent",
        '^' => "caret",
        '&' => "and",
        '*' => "star",
        '-' => "dash",
        '_' => "underscore",
        '+' => "plus",
        '=' => "equals",
        '~' => "tilde",
        '`' => "backtick",

        >= 'A' and <= 'Z' => $"cap {c}",

        _ => c.ToString(),
    };
}
