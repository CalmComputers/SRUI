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
    /// SliderChange, Filter) keep only the last occurrence of their kind —
    /// an intermediate focus move is discarded in favor of the settled one.
    /// Action events (Typing, TextNav, Clipboard, Announce) and all
    /// Activated/Callback/Tick events keep emission order. The surviving
    /// Focused event is additionally delivered last in the batch: focus
    /// describes where the user is now, and everything else in the batch
    /// describes what just happened — so a handler that closes a dialog
    /// and then announces its result is heard as result first, restored
    /// focus second, regardless of emission order.</summary>
    public static List<CoreEvent> Apply(List<CoreEvent> events)
    {
        // Pass 1: last occurrence index per state-event kind.
        Dictionary<Type, int>? lastIndex = null;
        for (var i = 0; i < events.Count; i++)
            if (StateKind(events[i]) is Type kind)
                (lastIndex ??= new Dictionary<Type, int>())[kind] = i;
        if (lastIndex is null)
            return events;

        // Pass 2: drop state events that aren't the last of their kind,
        // holding the settled focus back for the end.
        var result = new List<CoreEvent>(events.Count);
        CoreEvent? focused = null;
        for (var i = 0; i < events.Count; i++)
        {
            if (StateKind(events[i]) is Type kind && lastIndex[kind] != i)
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
