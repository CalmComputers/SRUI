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
            case AccessibilityEvent.Focused(_, var info, var contextLabels):
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

            case AccessibilityEvent.Announce(var text):
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

        // Name (optional — nameless widgets announce as "role value ...").
        if (!string.IsNullOrEmpty(info.Name))
            result.Append(info.Name);

        // Role (empty for role-less widgets — announces without a role word).
        if (info.Role.Length != 0)
        {
            if (result.Length != 0)
                result.Append(' ');
            result.Append(info.Role);
        }

        // Value.
        if (info.Value.Length != 0)
        {
            result.Append(' ');
            result.Append(info.Value);
        }

        // States, dynamic state text first (e.g. "filter ed", "no filter").
        if (info.StateText.Length != 0)
        {
            result.Append(' ');
            result.Append(info.StateText);
        }
        if ((info.States & WidgetStates.Disabled) != 0)
            result.Append(" unavailable");
        if ((info.States & WidgetStates.Required) != 0)
            result.Append(" required");
        if ((info.States & WidgetStates.Warning) != 0)
            result.Append(" warning");

        // Description.
        if (info.Description.Length != 0)
        {
            result.Append(' ');
            result.Append(info.Description);
        }

        // Shortcut — the first one attached, in spoken form ("alt w").
        if (info.Shortcuts.Count != 0)
        {
            result.Append(' ');
            result.Append(info.Shortcuts[0].DisplayName());
        }

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
