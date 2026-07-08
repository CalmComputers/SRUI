namespace Srui.Core;

/// <summary>A single item in the core's output stream. Accessibility
/// events describe what the user should perceive (readers render them);
/// the widget variants (Activated, Toggled, Changed…) describe what the
/// program should react to; Tick reports a ticker interval.</summary>
internal abstract record CoreEvent
{
    /// <summary>What the user should perceive. Consumed by readers.</summary>
    public sealed record Acc(AccessibilityEvent Event) : CoreEvent;

    /// <summary>The node was activated (Enter on a button, or the layer's
    /// primary/cancel widget was triggered).</summary>
    public sealed record Activated(NodeId Node) : CoreEvent;

    /// <summary>Secondary activation (Shift+Enter).</summary>
    public sealed record SecondaryActivated(NodeId Node) : CoreEvent;

    /// <summary>A checkbox was toggled; Checked is the new value.</summary>
    public sealed record Toggled(NodeId Node, bool Checked) : CoreEvent;

    /// <summary>The node's widget state changed (text edited, selection
    /// moved, slider adjusted).</summary>
    public sealed record Changed(NodeId Node) : CoreEvent;

    /// <summary>A ticker's interval elapsed (see Ui.AddTicker). Fires at
    /// most once per clock advance per ticker.</summary>
    public sealed record Tick(ulong Ticker) : CoreEvent;
}

internal enum TypingKind { Insert, Delete, DeleteWord }

internal enum SelectionKind { Selected, Unselected, Cleared, All }

internal enum Boundary { Top, Bottom, Left, Right }

internal enum ClipboardOp { Copy, Cut, Paste }

internal enum NavGranularity { Char, Word, LineEdge, TextEdge, LineUp, LineDown }

/// <summary>Structured payload describing what the user should perceive.
/// Each variant carries the data its non-speech consumers (braille, UIA,
/// sonification) will need; the label snapshot is taken at emission time
/// so readers never race the tree.</summary>
internal abstract record AccessibilityEvent
{
    /// <summary>Focus moved to a different widget. ContextLabels holds the
    /// names of Label-role siblings preceding the focused widget in child
    /// order — empty on ordinary focus moves, populated when the host asks
    /// for a context re-announcement after a view transition.</summary>
    public sealed record Focused(NodeId Node, WidgetLabel Label, List<string> ContextLabels)
        : AccessibilityEvent;

    /// <summary>Text was inserted or deleted in an editor. Carries only
    /// the perceptual content (what the user hears), not document text.
    /// Grapheme is a single grapheme for Insert/Delete and empty for
    /// DeleteWord; LastWord is set when an insert just crossed a word
    /// boundary or when the kind is DeleteWord.</summary>
    public sealed record Typing(NodeId Node, string Grapheme, string? LastWord, TypingKind Kind)
        : AccessibilityEvent;

    /// <summary>Cursor moved within text without an edit. Context is the
    /// spoken content for the landing position at this granularity — the
    /// expanded character, the word, or the line. BoundaryHit is set when
    /// nav was attempted past an edge and the cursor did not move.</summary>
    public sealed record TextNav(
        NodeId Node, string GraphemeAtCursor, string Context,
        NavGranularity Granularity, Boundary? BoundaryHit) : AccessibilityEvent;

    /// <summary>Selection extended, contracted, cleared, or set-all.
    /// Delta is the text added to or removed from the selection (or
    /// "{n} characters" for large selections).</summary>
    public sealed record Selection(NodeId Node, string Delta, SelectionKind Kind)
        : AccessibilityEvent;

    /// <summary>Discrete-value list/grid widget changed selection.
    /// Position is (zero-based index, total), or null when the widget has
    /// no indexable concept.</summary>
    public sealed record ItemNav(
        NodeId Node, string Item, (int Index, int Total)? Position, Boundary? BoundaryHit)
        : AccessibilityEvent;

    /// <summary>Tab control moved to a different tab. No boundary because
    /// tabs are circular.</summary>
    public sealed record TabChange(NodeId Node, string TabName, (int Index, int Total) Position)
        : AccessibilityEvent;

    /// <summary>Slider value changed.</summary>
    public sealed record SliderChange(NodeId Node, int Value, string Unit) : AccessibilityEvent;

    /// <summary>Filter / typeahead query changed; results recomputed.</summary>
    public sealed record Filter(NodeId Node, string Query, string? FirstResult, int ResultCount)
        : AccessibilityEvent;

    /// <summary>Clipboard operation completed.</summary>
    public sealed record Clipboard(NodeId Node, ClipboardOp Op) : AccessibilityEvent;

    /// <summary>Free-form announcement — the escape hatch for anything
    /// without a structured variant.</summary>
    public sealed record Announce(string Text) : AccessibilityEvent;
}

internal static class Coalesce
{
    /// <summary>Apply coalescing rules to a drained batch: state-describing
    /// accessibility events (Focused, Selection, ItemNav, TabChange,
    /// SliderChange, Filter) keep only the last occurrence of their kind —
    /// an intermediate focus move is discarded in favor of the settled one.
    /// Action events (Typing, TextNav, Clipboard, Announce) and all widget
    /// events keep emission order.</summary>
    public static List<CoreEvent> Apply(List<CoreEvent> events)
    {
        // Pass 1: last occurrence index per state-event kind.
        Dictionary<Type, int>? lastIndex = null;
        for (var i = 0; i < events.Count; i++)
            if (StateKind(events[i]) is Type kind)
                (lastIndex ??= new Dictionary<Type, int>())[kind] = i;
        if (lastIndex is null)
            return events;

        // Pass 2: drop state events that aren't the last of their kind.
        var result = new List<CoreEvent>(events.Count);
        for (var i = 0; i < events.Count; i++)
        {
            if (StateKind(events[i]) is Type kind && lastIndex[kind] != i)
                continue;
            result.Add(events[i]);
        }
        return result;
    }

    private static Type? StateKind(CoreEvent ev) => ev is CoreEvent.Acc(var acc) && acc
        is AccessibilityEvent.Focused
        or AccessibilityEvent.Selection
        or AccessibilityEvent.ItemNav
        or AccessibilityEvent.TabChange
        or AccessibilityEvent.SliderChange
        or AccessibilityEvent.Filter
        ? acc.GetType()
        : null;
}
