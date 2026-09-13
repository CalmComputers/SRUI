namespace Srui;

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
    NothingToUndo,
    NothingToRedo,
}

/// <summary>Why focus arrived — the provenance readers filter on (an
/// earcon reader clicks on user movement but not on a dialog opening;
/// a verbosity policy may say more on recovery).</summary>
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

/// <summary>One enclosing context spoken ahead of a focus arrival: a
/// group focus entered (its name and role), or a Label preceding the
/// widget (its name, role Label) when the arrival carries context
/// labels — a dialog opening.</summary>
public readonly record struct ContextEntry(string? Name, Role Role);

/// <summary>Structured payload describing what the user should perceive.
/// Readers consume these in ticks (<see cref="IReader.OnTick"/>); the
/// reference speech rendering lives in <see cref="SpeechRenderer"/>.
/// Every event names the widget it concerns as <see cref="Source"/>;
/// an app-level <see cref="Announce"/> has none. A widget's events reach
/// readers only when that widget is where focus settled at the end of
/// the tick — a widget the user is not on says nothing.</summary>
public abstract record AccessibilityEvent(Widget? Source)
{
    /// <summary>Focus settled on a different widget than the user last
    /// heard (or a re-announcement was asked for). The tick's
    /// <see cref="FieldValue"/> entries that follow carry the widget's
    /// fields in full. Context lists the groups entered on the way,
    /// outermost first, and the preceding labels when the arrival
    /// carries them.</summary>
    public sealed record FocusArrived(Widget Widget, FocusCause Cause, IReadOnlyList<ContextEntry> Context)
        : AccessibilityEvent(Widget);

    /// <summary>One field of the focused widget, or of the item under
    /// its cursor: after a <see cref="FocusArrived"/>, every field;
    /// otherwise a field that changed since the user last heard it (or
    /// one the widget asked to have reread). Readers render the value
    /// through their own tables.</summary>
    public sealed record FieldValue(Widget Widget, Field Field, object? Value, FieldScope Scope)
        : AccessibilityEvent(Widget);

    /// <summary>Navigation ran into an edge and nothing moved. The
    /// widget asks for its position to be reread alongside, so the
    /// reader can say "top, Apple".</summary>
    public sealed record BoundaryHit(Widget Widget, Boundary Edge) : AccessibilityEvent(Widget);

    /// <summary>Text was inserted or deleted in an editor. Carries only
    /// the perceptual content (what the user hears), not document text.
    /// Grapheme is a single grapheme for Insert/Delete and empty for
    /// DeleteWord; LastWord is set when an insert just crossed a word
    /// boundary or when the kind is DeleteWord.</summary>
    public sealed record Typing(Widget Widget, string Grapheme, string? LastWord, TypingKind Kind)
        : AccessibilityEvent(Widget);

    /// <summary>Cursor moved within text without an edit. Context is the
    /// spoken content for the landing position at this granularity — the
    /// expanded character, the word, or the line. Edge is set when
    /// nav was attempted past an edge and the cursor did not move.</summary>
    public sealed record TextNav(
        Widget Widget, string GraphemeAtCursor, string Context,
        NavGranularity Granularity, Boundary? Edge) : AccessibilityEvent(Widget);

    /// <summary>Selection extended, contracted, cleared, or set-all.
    /// Delta is the text added to or removed from the selection (or
    /// "{n} characters" for large selections).</summary>
    public sealed record Selection(Widget Widget, string Delta, SelectionKind Kind)
        : AccessibilityEvent(Widget);

    /// <summary>Clipboard operation completed.</summary>
    public sealed record Clipboard(Widget Widget, ClipboardOp Op) : AccessibilityEvent(Widget);

    /// <summary>An undo or redo was applied in an editor. Context is
    /// the spoken value at the restored state — the restored selection
    /// (Selected true) or the current line.</summary>
    public sealed record UndoRedo(Widget Widget, string Context, bool Selected, bool Redo)
        : AccessibilityEvent(Widget);

    /// <summary>The cursor of the focused widget landed on another item
    /// (<see cref="Widget.CurrentItem"/>). The item-scope
    /// <see cref="FieldValue"/> entries that follow carry the landed
    /// item's fields in full.</summary>
    public sealed record ItemArrived(Widget Widget, Element Item) : AccessibilityEvent(Widget);

    /// <summary>An editor input with nothing to act on: navigation or
    /// selection in an empty editor, a delete with nothing beside the
    /// cursor, or a selection already pinned at a text edge. Context is
    /// the spoken content at the cursor for the pinned-selection kinds,
    /// null otherwise.</summary>
    public sealed record EditNoop(Widget Widget, EditNoopKind Kind, string? Context = null)
        : AccessibilityEvent(Widget);

    /// <summary>Free-form announcement — the one place prose crosses
    /// from the program to the reader. Spoken in the order emitted,
    /// before the tick's state. With a Source it is the widget's and
    /// speaks only when the widget is where focus settled; without one
    /// it is the app's and always speaks.</summary>
    public sealed record Announce(string Text, Widget? Source = null) : AccessibilityEvent(Source);
}

/// <summary>A consumer of accessibility events: self-voicing speech,
/// braille, a platform accessibility bridge, a test recorder. Readers are
/// attached with <see cref="SruiApp.AddReader"/> and receive one call
/// per tick with everything the tick produced.</summary>
public interface IReader
{
    /// <summary>The events of one tick, in order: announcements and
    /// action events as they happened, then the settled state — a
    /// <see cref="AccessibilityEvent.FocusArrived"/> and the widget's
    /// fields when focus moved, else the fields that changed. The list
    /// is the engine's and valid for the duration of the call; copy it
    /// to keep it.</summary>
    void OnTick(IReadOnlyList<AccessibilityEvent> events);

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

    /// <summary>A system-wide hotkey registered through
    /// <see cref="ISystemHotkeys"/> was pressed, wherever keyboard focus
    /// was. The combo never arrives as a key: the OS consumed it.</summary>
    public sealed record Hotkey(int Id) : HostEvent;
}
