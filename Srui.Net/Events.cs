namespace Srui;

/// <summary>Widget states — conditions the user cannot directly change.</summary>
[Flags]
public enum WidgetStates : uint
{
    None = 0,
    Disabled = 1 << 0,
    Required = 1 << 1,
    Warning = 1 << 2,
    Hidden = 1 << 3,
    /// <summary>The widget carries <see cref="Widget.KeyHelp"/> text;
    /// spoken "with help", read on F1.</summary>
    WithHelp = 1 << 4,
}

/// <summary>What a Typing event describes.</summary>
public enum TypingKind
{
    Insert,
    Delete,
    DeleteWord,
}

/// <summary>What a Selection event describes.</summary>
public enum SelectionKind
{
    Selected,
    Unselected,
    Cleared,
    All,
}

/// <summary>An edge the user ran into (or landed on) while navigating.</summary>
public enum Boundary
{
    Top,
    Bottom,
    Left,
    Right,
}

public enum ClipboardOp
{
    Copy,
    Cut,
    Paste,
}

/// <summary>What an EditNoop event reports — the editor input that had
/// nothing to act on.</summary>
public enum EditNoopKind
{
    NoText,
    NothingToSelect,
    NothingToDelete,
    SelectedToTop,
    SelectedToBottom,
}

/// <summary>The label property a LabelChange event names.</summary>
public enum LabelPart
{
    Name,
    Role,
    Value,
    Description,
}

/// <summary>Why a Focused event was emitted — the provenance readers
/// filter on (an earcon reader clicks on user movement but not on a
/// dialog opening; a verbosity policy may say more on recovery).</summary>
public enum FocusCause
{
    /// <summary>The user moved: the tab ring or hierarchy navigation.</summary>
    UserNavigation,
    /// <summary>A widget shortcut jumped focus (mnemonics included).</summary>
    Shortcut,
    /// <summary>The program moved focus: Widget.Focus() or EnsureFocus.</summary>
    Programmatic,
    /// <summary>Focus recovered after the focused node was removed or
    /// became unreachable.</summary>
    Recovery,
    /// <summary>A layer was popped and the previous layer's focus
    /// restored.</summary>
    LayerRestore,
    /// <summary>Focus did not move: a re-announcement of the focused
    /// widget (speak-focus, or the with-context announcement after a
    /// view transition).</summary>
    Reannounce,
}

/// <summary>Granularity of a text-cursor move.</summary>
public enum NavGranularity
{
    Char,
    Word,
    LineEdge,
    TextEdge,
    LineUp,
    LineDown,
}

/// <summary>A snapshot of the six semantic properties a screen reader
/// announces, in NVDA order: name, role, value, states, description,
/// shortcut. Role is the spoken role text (empty for role-less widgets);
/// StateText is dynamic state spoken before the flag states (e.g. "no
/// filter"); Shortcuts lists every combo attached to the widget, first
/// added first (announcements speak the first). Taken at emission time,
/// so readers never race the tree.</summary>
public sealed record WidgetInfo(
    string? Name,
    string Role,
    string Value,
    string StateText,
    WidgetStates States,
    string Description,
    IReadOnlyList<KeyCombo> Shortcuts);

/// <summary>Structured payload describing what the user should perceive.
/// Readers consume these; the reference speech rendering lives in
/// <see cref="SpeechRenderer"/>. Every variant carries the widget it
/// concerns; on Announce the source is optional (null for app-level
/// announcements).</summary>
public abstract record AccessibilityEvent
{
    /// <summary>Focus moved to (or was re-announced on) a widget. Info is
    /// the golden-six snapshot at emission. ContextLabels holds the names
    /// of Label siblings preceding the widget in child order — empty on
    /// ordinary focus moves, populated for context re-announcements after
    /// a view transition (dialog openings). Cause is the provenance of
    /// the move (user navigation, shortcut jump, programmatic, recovery,
    /// layer restore, re-announcement), so readers can treat user
    /// movement differently from focus that came to the user.</summary>
    public sealed record Focused(
        Widget Widget, WidgetInfo Info, IReadOnlyList<string> ContextLabels, FocusCause Cause)
        : AccessibilityEvent;

    /// <summary>Text was inserted or deleted in an editor. Carries only
    /// the perceptual content (what the user hears), not document text.
    /// Grapheme is a single grapheme for Insert/Delete and empty for
    /// DeleteWord; LastWord is set when an insert just crossed a word
    /// boundary or when the kind is DeleteWord.</summary>
    public sealed record Typing(Widget Widget, string Grapheme, string? LastWord, TypingKind Kind)
        : AccessibilityEvent;

    /// <summary>Cursor moved within text without an edit. Context is the
    /// spoken content for the landing position at this granularity — the
    /// expanded character, the word, or the line. BoundaryHit is set when
    /// nav was attempted past an edge and the cursor did not move.</summary>
    public sealed record TextNav(
        Widget Widget, string GraphemeAtCursor, string Context,
        NavGranularity Granularity, Boundary? BoundaryHit) : AccessibilityEvent;

    /// <summary>Selection extended, contracted, cleared, or set-all.
    /// Delta is the text added to or removed from the selection (or
    /// "{n} characters" for large selections).</summary>
    public sealed record Selection(Widget Widget, string Delta, SelectionKind Kind)
        : AccessibilityEvent;

    /// <summary>Discrete-value list/grid widget changed selection.
    /// Position is (zero-based index, total), or null when the widget has
    /// no indexable concept.</summary>
    public sealed record ItemNav(
        Widget Widget, string Item, (int Index, int Total)? Position, Boundary? BoundaryHit)
        : AccessibilityEvent;

    /// <summary>Tab control moved to a different tab. No boundary because
    /// tabs are circular.</summary>
    public sealed record TabChange(Widget Widget, string TabName, (int Index, int Total) Position)
        : AccessibilityEvent;

    /// <summary>Slider value changed.</summary>
    public sealed record SliderChange(Widget Widget, int Value, string Unit) : AccessibilityEvent;

    /// <summary>Check box toggled — by the user, or programmatically
    /// while focused. The reference rendering speaks the new state alone
    /// ("checked", "not checked").</summary>
    public sealed record Toggle(Widget Widget, bool Checked) : AccessibilityEvent;

    /// <summary>Filter / typeahead query changed; results recomputed.</summary>
    public sealed record Filter(Widget Widget, string Query, string? FirstResult, int ResultCount)
        : AccessibilityEvent;

    /// <summary>Clipboard operation completed.</summary>
    public sealed record Clipboard(Widget Widget, ClipboardOp Op) : AccessibilityEvent;

    /// <summary>An editor input with nothing to act on: navigation or
    /// selection in an empty editor, a delete with nothing beside the
    /// cursor, or a selection already pinned at a text edge. Context is
    /// the spoken content at the cursor for the pinned-selection kinds,
    /// null otherwise.</summary>
    public sealed record EditNoop(Widget Widget, EditNoopKind Kind, string? Context = null)
        : AccessibilityEvent;

    /// <summary>A label property of the focused widget changed
    /// programmatically. Text is the property's new spoken text; the
    /// reference rendering speaks it alone — the delta, never a full
    /// re-announcement. Label changes on unfocused widgets are silent
    /// and produce no event.</summary>
    public sealed record LabelChange(Widget Widget, LabelPart Part, string Text)
        : AccessibilityEvent;

    /// <summary>A state flag of the focused widget was set or cleared
    /// programmatically ("disabled", "required", "warning cleared").
    /// Hidden never appears here: hiding the focused widget reads as the
    /// focus recovery it causes.</summary>
    public sealed record StateChange(Widget Widget, WidgetStates State, bool On)
        : AccessibilityEvent;

    /// <summary>Free-form announcement — the escape hatch for anything
    /// without a structured variant. Source is the widget that emitted
    /// it, or null for app-level announcements.</summary>
    public sealed record Announce(string Text, Widget? Source = null) : AccessibilityEvent;
}

/// <summary>A consumer of accessibility events: self-voicing speech,
/// braille, a platform accessibility bridge, a test recorder. Readers are
/// attached with <see cref="SruiApp.AddReader"/> and receive every
/// accessibility event the drain delivers, in order.</summary>
public interface IReader
{
    void OnEvent(AccessibilityEvent e);

    /// <summary>The user acted (a key went down, or the app demanded
    /// urgency): whatever is being presented is stale. The speech reader
    /// silences here; readers with no notion of interruption ignore it.</summary>
    void OnInterrupt()
    {
    }
}

/// <summary>One pumped host event.</summary>
public abstract record HostEvent
{
    public sealed record Quit : HostEvent;

    /// <summary>A physical key went down; readers are interrupted before
    /// the corresponding Input is handled.</summary>
    public sealed record KeyDown : HostEvent;

    /// <summary>A clean Alt tap (commonly bound to a menu/palette).</summary>
    public sealed record AltTap : HostEvent;

    public sealed record Input(InputEvent Event) : HostEvent;

    /// <summary>A physical key transition (press, repeat, or release),
    /// parallel to and independent of the Input stream.</summary>
    public sealed record Key(KeyInput Event) : HostEvent;

    /// <summary>The window lost keyboard focus. Held-key releases will
    /// not arrive; zero any held-key state.</summary>
    public sealed record FocusLost : HostEvent;
}
