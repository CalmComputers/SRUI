using System.Text;

namespace Srui;

/// <summary>What typing speaks as it lands. Gates insertion only:
/// deletions always speak what they removed — that is confirmation of
/// a destruction, not typing chatter.</summary>
public enum TypingEcho
{
    /// <summary>Every character, and the word it completed on a
    /// separator: "hello space". The default.</summary>
    Both,

    /// <summary>Every character; no completed words.</summary>
    Characters,

    /// <summary>Only the completed word, on the separator or Enter
    /// that finished it.</summary>
    Words,

    /// <summary>Nothing.</summary>
    None,
}

/// <summary>What the widget under a closed dialog announces when focus
/// returns to it. The user never left the widget, so this is
/// re-orientation rather than news, and how much of it to speak is an
/// experience curve: the full announcement re-anchors a user who lost
/// the thread while the dialog was up, the delta serves one who trusts
/// that silence means nothing changed, and nothing serves the expert
/// whose hands already know where they are. Whatever the mode, a
/// result the dialog announced before closing still speaks, and a
/// restore whose widget is gone reads in full as the recovery it
/// is.</summary>
public enum RestoreAnnouncement
{
    /// <summary>The normal focus announcement, under the same
    /// verbosity as any other focus. The default.</summary>
    Full,

    /// <summary>Only what changed while the dialog was open — value
    /// and state; an unchanged widget restores in silence.</summary>
    Changes,

    /// <summary>Nothing.</summary>
    None,
}

/// <summary>What the reference rendering speaks beyond the essentials.
/// Everything on is the default and the historical behavior. A reader
/// holds one instance (<see cref="SpeechReader.Verbosity"/>) and the
/// app mutates its fields live; the renderer consults it per event.
/// Name, value, dynamic state text, and the actionable states
/// (unavailable, required, warning) are never suppressible — those
/// are information, not verbosity. <see cref="Restore"/> is the one
/// whole-utterance switch, and it may trim information: the return
/// from a closed dialog is a re-orientation the user can opt out of
/// wholesale, not a fact they never chose to hear.</summary>
public sealed class SpeechVerbosity
{
    /// <summary>Speak widget roles ("button", "slider").</summary>
    public bool Roles = true;

    /// <summary>Speak the widget's shortcut suffix ("alt h").</summary>
    public bool Shortcuts = true;

    /// <summary>Speak descriptions and the "with help" hint.</summary>
    public bool Extras = true;

    /// <summary>What typing speaks as it lands.</summary>
    public TypingEcho Echo = TypingEcho.Both;

    /// <summary>What the widget under a closed dialog announces when
    /// focus returns to it. Consulted at the pop itself, not just the
    /// rendering: under <see cref="RestoreAnnouncement.Changes"/> an
    /// unchanged widget emits no Focused event at all, and under
    /// <see cref="RestoreAnnouncement.None"/> none ever does.</summary>
    public RestoreAnnouncement Restore = RestoreAnnouncement.Full;
}

/// <summary>Speech rendering — the reference rendering of accessibility
/// events. Pure functions from structured events to utterance strings.
/// The self-voicing <see cref="SpeechReader"/> uses these; braille and
/// platform readers ignore them and read the structured payloads
/// directly; tests assert against them. The optional
/// <see cref="SpeechVerbosity"/> trims the rendering; omitted, the
/// full form is spoken.</summary>
public static class SpeechRenderer
{
    private static readonly SpeechVerbosity Full = new();
    /// <summary>Maximum characters to announce verbatim for selections;
    /// beyond this we say "N characters selected".</summary>
    public const int SpeakLimit = 500;

    /// <summary>Render an accessibility event to an utterance at full
    /// verbosity. Null when there is nothing sensible to say (readers
    /// skip silently). An overload, not a default parameter, so the
    /// method group still converts to Func — tests map event lists
    /// through it.</summary>
    public static string? RenderEvent(AccessibilityEvent ev) => RenderEvent(ev, Full);

    /// <summary>Render an accessibility event to an utterance under a
    /// verbosity (null means full). Null when there is nothing
    /// sensible to say (readers skip silently).</summary>
    public static string? RenderEvent(AccessibilityEvent ev, SpeechVerbosity? verbosity)
    {
        verbosity ??= Full;
        switch (ev)
        {
            case AccessibilityEvent.Focused(_, var info, var contextLabels, var cause):
            {
                // A layer restore returns the user to the widget they
                // never left; the verbosity's Restore mode says how
                // much of a re-orientation that is worth. Full is the
                // ordinary focus announcement; Changes arrives only
                // when something changed under the closed layer and
                // speaks value and state, the news without the
                // identity; None never arrives, and renders null as
                // the backstop.
                if (cause == FocusCause.LayerRestore)
                    return verbosity.Restore switch
                    {
                        RestoreAnnouncement.Changes => AnnounceRestore(info),
                        RestoreAnnouncement.None => null,
                        _ => AnnounceFocus(info, verbosity),
                    };
                var announcement = AnnounceFocus(info, verbosity);
                return contextLabels.Count == 0
                    ? announcement
                    : $"{string.Join(" ", contextLabels)} {announcement}";
            }

            case AccessibilityEvent.Typing(_, var grapheme, var lastWord, var kind):
            {
                // On a word boundary the just-completed word is echoed
                // before the separator: "hello space". Echo gates
                // insertion only; deletions always speak. For deletes,
                // grapheme is already in spoken form (SpeakChar passes
                // multi-char strings through unchanged).
                var chars = verbosity.Echo is TypingEcho.Both or TypingEcho.Characters;
                var words = verbosity.Echo is TypingEcho.Both or TypingEcho.Words;
                return kind switch
                {
                    TypingKind.Insert when words && lastWord is not null && chars =>
                        $"{lastWord} {SpeakChar(grapheme)}",
                    TypingKind.Insert when words && lastWord is not null => lastWord,
                    TypingKind.Insert when chars && grapheme.Length != 0 => SpeakChar(grapheme),
                    TypingKind.Insert => null,
                    TypingKind.Delete when grapheme.Length != 0 => SpeakChar(grapheme),
                    TypingKind.Delete => null,
                    TypingKind.DeleteWord => lastWord,
                    _ => null,
                };
            }

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

            case AccessibilityEvent.ItemNav(_, var item, var position, var boundary, var isChecked):
            {
                // Checked state rides between the item and its position;
                // unchecked items say nothing (the absence is the signal).
                var line = isChecked == true ? $"{item} checked" : item;
                var baseText = position is (var index, var total)
                    ? $"{line} {index + 1} of {total}"
                    : line;
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

            // The same words the focus announcement's value field uses,
            // unless the widget supplied the utterance wholesale.
            case AccessibilityEvent.Toggle(_, var isChecked) toggle:
                return toggle.Words ?? (isChecked ? "checked" : "not checked");

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
            // or description) has nothing to speak. A part the
            // verbosity suppresses from focus announcements stays
            // suppressed here — the echo of a fact never spoken.
            case AccessibilityEvent.LabelChange(_, var part, var newText):
                if (part == LabelPart.Description && !verbosity.Extras)
                    return null;
                if (part == LabelPart.Role && !verbosity.Roles)
                    return null;
                return newText.Length == 0 ? null : newText;

            case AccessibilityEvent.StateChange(_, var state, var on):
                return state switch
                {
                    // The same word the focus announcement uses.
                    WidgetStates.Disabled => on ? "unavailable" : "available",
                    WidgetStates.Required => on ? "required" : "not required",
                    WidgetStates.Warning => on ? "warning" : "warning cleared",
                    WidgetStates.WithHelp when !verbosity.Extras => null,
                    WidgetStates.WithHelp => on ? "with help" : "help removed",
                    _ => null,
                };

            case AccessibilityEvent.EditNoop(_, var kind, var context):
                return kind switch
                {
                    EditNoopKind.NoText => "No text",
                    EditNoopKind.NothingToSelect => "Nothing to select",
                    EditNoopKind.NothingToDelete => "Nothing to delete",
                    EditNoopKind.NothingToUndo => "Nothing to undo",
                    EditNoopKind.NothingToRedo => "Nothing to redo",
                    EditNoopKind.SelectedToTop => $"Already selected to top, {context}",
                    _ => $"Already selected to bottom, {context}",
                };

            // Where the user now stands — the selection that came back,
            // or the line as it now reads. The words say what changed;
            // "Nothing to undo" already covers the failure case.
            case AccessibilityEvent.UndoRedo(_, var undoContext, _):
                return undoContext;

            case AccessibilityEvent.Announce(var text, _):
                return text;

            default:
                return null;
        }
    }

    /// <summary>Construct a focus announcement from the golden six at
    /// full verbosity, in NVDA ordering: Name Role Value States
    /// Description Shortcut. No commas between fields; shortcuts
    /// spoken as "alt s" not "Alt+S".</summary>
    public static string AnnounceFocus(WidgetInfo info) => AnnounceFocus(info, Full);

    /// <summary>Construct a focus announcement under a verbosity (null
    /// means full): it trims role, "with help", description, and the
    /// shortcut; the rest always speaks.</summary>
    public static string AnnounceFocus(WidgetInfo info, SpeechVerbosity? verbosity)
    {
        verbosity ??= Full;
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
        if (verbosity.Roles)
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
        if ((info.States & WidgetStates.WithHelp) != 0 && verbosity.Extras)
            Append("with help");

        // Description.
        if (verbosity.Extras)
            Append(info.Description);

        // Shortcut — the first one attached, in spoken form ("alt w").
        if (verbosity.Shortcuts && info.Shortcuts.Count != 0)
            Append(info.Shortcuts[0].DisplayName());

        return result.ToString();
    }

    /// <summary>The layer-restore announcement under
    /// <see cref="RestoreAnnouncement.Changes"/>: value and dynamic
    /// state, with the actionable state flags - never the name, role,
    /// description, or shortcut the user heard before the layer opened.
    /// Null when nothing audible remains.</summary>
    public static string? AnnounceRestore(WidgetInfo info)
    {
        var result = new StringBuilder(32);
        void Append(string field)
        {
            if (field.Length == 0)
                return;
            if (result.Length != 0)
                result.Append(' ');
            result.Append(field);
        }
        Append(info.Value);
        Append(info.StateText);
        if ((info.States & WidgetStates.Disabled) != 0)
            Append("unavailable");
        if ((info.States & WidgetStates.Required) != 0)
            Append("required");
        if ((info.States & WidgetStates.Warning) != 0)
            Append("warning");
        return result.Length == 0 ? null : result.ToString();
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
        '?' => "question",
        '\'' => "tick",
        '"' => "quote",
        '(' => "left paren",
        ')' => "right paren",
        '[' => "left bracket",
        ']' => "right bracket",
        '{' => "left brace",
        '}' => "right brace",
        '<' => "less",
        '>' => "greater",
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
        '_' => "line",
        '+' => "plus",
        '=' => "equals",
        '~' => "tilde",
        '`' => "graav",

        >= 'A' and <= 'Z' => $"cap {c}",

        _ => c.ToString(),
    };
}
