using System.Text;

namespace Srui.Core;

/// <summary>Speech rendering — the reference rendering of accessibility
/// events. Pure functions from structured events to utterance strings.
/// The self-voicing reader uses these; braille and UIA readers ignore
/// them and read the structured payloads directly.</summary>
internal static class SpeechRenderer
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
            case AccessibilityEvent.Focused(_, var label, var contextLabels):
            {
                var announcement = AnnounceFocus(label);
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

    /// <summary>The SpeechSource tag for the public event surface.</summary>
    public static SpeechSource SourceOf(AccessibilityEvent ev) => ev switch
    {
        AccessibilityEvent.Focused => SpeechSource.Focused,
        AccessibilityEvent.Typing => SpeechSource.Typing,
        AccessibilityEvent.TextNav => SpeechSource.TextNav,
        AccessibilityEvent.Selection => SpeechSource.Selection,
        AccessibilityEvent.ItemNav => SpeechSource.ItemNav,
        AccessibilityEvent.TabChange => SpeechSource.TabChange,
        AccessibilityEvent.SliderChange => SpeechSource.SliderChange,
        AccessibilityEvent.Filter => SpeechSource.Filter,
        AccessibilityEvent.Clipboard => SpeechSource.Clipboard,
        _ => SpeechSource.Announce,
    };

    /// <summary>The node an accessibility event concerns, or None for
    /// free-form announcements.</summary>
    public static NodeId NodeOf(AccessibilityEvent ev) => ev switch
    {
        AccessibilityEvent.Focused e => e.Node,
        AccessibilityEvent.Typing e => e.Node,
        AccessibilityEvent.TextNav e => e.Node,
        AccessibilityEvent.Selection e => e.Node,
        AccessibilityEvent.ItemNav e => e.Node,
        AccessibilityEvent.TabChange e => e.Node,
        AccessibilityEvent.SliderChange e => e.Node,
        AccessibilityEvent.Filter e => e.Node,
        AccessibilityEvent.Clipboard e => e.Node,
        _ => NodeId.None,
    };

    /// <summary>Construct a focus announcement from the golden six, in
    /// NVDA ordering: Name Role Value States Description Shortcut. No
    /// commas between fields; shortcuts spoken as "alt s" not "Alt+S".</summary>
    public static string AnnounceFocus(WidgetLabel label)
    {
        var result = new StringBuilder(64);

        // Name (optional — nameless widgets announce as "role value ...").
        if (!string.IsNullOrEmpty(label.Name))
            result.Append(label.Name);

        // Role (empty for Custom — announces without a role word).
        var roleText = label.Role.ToSpeech();
        if (roleText.Length != 0)
        {
            if (result.Length != 0)
                result.Append(' ');
            result.Append(roleText);
        }

        // Value.
        if (label.Value.Length != 0)
        {
            result.Append(' ');
            result.Append(label.Value);
        }

        // States (excluding Focused — focus is implicit), dynamic state
        // text first (e.g. "filter ed", "no filter").
        if (label.StateText.Length != 0)
        {
            result.Append(' ');
            result.Append(label.StateText);
        }
        if ((label.States & States.Disabled) != 0)
            result.Append(" unavailable");
        if ((label.States & States.Required) != 0)
            result.Append(" required");
        if ((label.States & States.Warning) != 0)
            result.Append(" warning");

        // Description.
        if (label.Description.Length != 0)
        {
            result.Append(' ');
            result.Append(label.Description);
        }

        // Shortcut — the first one attached, in spoken form ("alt w").
        if (label.Shortcuts.Count != 0)
        {
            result.Append(' ');
            result.Append(label.Shortcuts[0].Combo.DisplayName());
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
