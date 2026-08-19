namespace Srui.Core;

/// <summary>A single item in the engine's output queue. Accessibility
/// events describe what the user should perceive (readers render them);
/// Activated names a node the framework triggered (primary/cancel
/// routing, activate shortcuts, or the widget's own press); Callback is a
/// widget-queued program reaction, delivered at drain so handlers never
/// run inside input dispatch; Tick reports a ticker interval.</summary>
internal abstract record CoreEvent
{
    /// <summary>What the user should perceive. Consumed by readers.</summary>
    public sealed record Acc(AccessibilityEvent Event) : CoreEvent;

    /// <summary>The node was activated. Dispatch resolves the owning
    /// widget and calls its OnActivated.</summary>
    public sealed record Activated(NodeId Node) : CoreEvent;

    /// <summary>A deferred program notification (Changed, Toggled, custom
    /// widget events). Invoked in order at drain time.</summary>
    public sealed record Callback(Action Invoke) : CoreEvent;

    /// <summary>A ticker's interval elapsed (see SruiApp.StartTicker).
    /// Fires at most once per clock advance per ticker.</summary>
    public sealed record Tick(ulong Ticker) : CoreEvent;
}

internal static class Coalesce
{
    /// <summary>Apply coalescing rules to a drained batch: state-describing
    /// accessibility events (Focused, Selection, ItemNav, TabChange,
    /// SliderChange, Toggle, Filter, and the label deltas
    /// LabelChange/StateChange) keep only the last occurrence of their
    /// kind — an intermediate focus move is discarded in favor of the
    /// settled one. The label deltas key per property (and per state
    /// flag), so a rename and a description change in one batch both
    /// survive while two renames collapse to the settled name. Action
    /// events (Typing, TextNav, Clipboard, EditNoop, Announce) and all
    /// Activated/Callback/Tick events keep emission order. The
    /// surviving Focused event is additionally delivered last in the
    /// batch: focus describes where the user is now, and everything else
    /// in the batch describes what just happened — so a handler that
    /// closes a dialog and then announces its result is heard as result
    /// first, restored focus second, regardless of emission order.</summary>
    public static List<CoreEvent> Apply(List<CoreEvent> events)
    {
        // Pass 1: last occurrence index per state-event kind.
        Dictionary<(Type, int), int>? lastIndex = null;
        for (var i = 0; i < events.Count; i++)
            if (StateKind(events[i]) is { } kind)
                (lastIndex ??= new Dictionary<(Type, int), int>())[kind] = i;
        if (lastIndex is null)
            return events;

        // Pass 2: drop state events that aren't the last of their kind,
        // holding the settled focus back for the end.
        var result = new List<CoreEvent>(events.Count);
        CoreEvent? focused = null;
        for (var i = 0; i < events.Count; i++)
        {
            if (StateKind(events[i]) is { } kind && lastIndex[kind] != i)
                continue;
            if (events[i] is CoreEvent.Acc(AccessibilityEvent.Focused))
                focused = events[i];
            else
                result.Add(events[i]);
        }
        if (focused is not null)
            result.Add(focused);
        return result;
    }

    private static (Type, int)? StateKind(CoreEvent ev)
    {
        if (ev is not CoreEvent.Acc(var acc))
            return null;
        return acc switch
        {
            AccessibilityEvent.Focused
                or AccessibilityEvent.Selection
                or AccessibilityEvent.ItemNav
                or AccessibilityEvent.TabChange
                or AccessibilityEvent.SliderChange
                or AccessibilityEvent.Toggle
                or AccessibilityEvent.Filter => (acc.GetType(), 0),
            AccessibilityEvent.LabelChange(_, var part, _) => (acc.GetType(), (int)part),
            AccessibilityEvent.StateChange(_, var state, _) => (acc.GetType(), (int)state),
            _ => null,
        };
    }
}
