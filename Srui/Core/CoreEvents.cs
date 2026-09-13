namespace Srui.Core;

/// <summary>A single item in the engine's output queue. Accessibility
/// events describe what the user should perceive (readers render them);
/// Activated names a node the framework triggered (primary/cancel
/// routing, activate shortcuts, or the widget's own press); Callback is a
/// widget-queued program reaction, delivered at drain so handlers never
/// run inside input dispatch.</summary>
internal abstract record CoreEvent
{
    /// <summary>What the user should perceive. Collected into the tick
    /// and delivered to readers at its end.</summary>
    public sealed record Acc(AccessibilityEvent Event) : CoreEvent;

    /// <summary>The node was activated. Dispatch resolves the owning
    /// widget and calls its OnActivated.</summary>
    public sealed record Activated(NodeId Node) : CoreEvent;

    /// <summary>A deferred program notification (Changed, Toggled, custom
    /// widget events). Invoked in order at drain time.</summary>
    public sealed record Callback(Action Invoke) : CoreEvent;
}
