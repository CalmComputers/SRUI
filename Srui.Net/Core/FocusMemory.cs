namespace Srui.Core;

/// <summary>Remembers the last-focused child per container so re-entering
/// a container restores position. Keyed by NodeId: entries die with their
/// nodes and are swept by <see cref="Gc"/>.</summary>
internal sealed class FocusMemory
{
    private readonly Dictionary<NodeId, NodeId> _memory = new();

    /// <summary>Record that <paramref name="child"/> was the last focused
    /// widget inside <paramref name="container"/>.</summary>
    public void Remember(NodeId container, NodeId child) => _memory[container] = child;

    /// <summary>The last focused child of the container, or None.</summary>
    public NodeId Recall(NodeId container) => _memory.GetValueOrDefault(container, NodeId.None);

    /// <summary>Drop entries whose container or child no longer exists.</summary>
    public void Gc(Tree tree)
    {
        List<NodeId>? dead = null;
        foreach (var (container, child) in _memory)
            if (!tree.Contains(container) || !tree.Contains(child))
                (dead ??= new List<NodeId>()).Add(container);
        if (dead is not null)
            foreach (var container in dead)
                _memory.Remove(container);
    }
}
