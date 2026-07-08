namespace Srui.Core;

/// <summary>A single node in the semantic tree.</summary>
internal sealed class Node
{
    public readonly NodeId Id;
    /// <summary>None for a layer root.</summary>
    public NodeId Parent;
    public readonly List<NodeId> Children = new();
    public WidgetLabel Label;

    public Node(NodeId id, NodeId parent, WidgetLabel label)
    {
        Id = id;
        Parent = parent;
        Label = label;
    }
}

/// <summary>The semantic tree — retained, handle-addressed, layered for
/// modals. Node ids are never reused (a monotonic counter), so an id held
/// after its node is removed resolves to nothing. Layers stack:
/// dialogs/palettes push a layer; the active layer is always last.</summary>
internal sealed class Tree
{
    private sealed class Layer
    {
        public readonly List<NodeId> Roots = new();
        public NodeId Focus = NodeId.None;
        public NodeId Primary = NodeId.None;
        public NodeId Cancel = NodeId.None;
    }

    private readonly Dictionary<NodeId, Node> _nodes = new();
    private readonly List<Layer> _layers = new() { new Layer() };
    private ulong _nextId = 1;

    /// <summary>Insert a new node as a child of <paramref name="parent"/>
    /// at <paramref name="index"/> (clamped). NodeId.None inserts as a
    /// root of the active layer; int.MaxValue appends.</summary>
    public NodeId Insert(NodeId parent, int index, WidgetLabel label)
    {
        var id = new NodeId(_nextId++);
        _nodes[id] = new Node(id, parent, label);

        if (!parent.IsNone)
        {
            if (_nodes.TryGetValue(parent, out var parentNode))
            {
                var idx = Math.Min(index, parentNode.Children.Count);
                parentNode.Children.Insert(idx, id);
            }
        }
        else
        {
            var roots = ActiveLayer.Roots;
            var idx = Math.Min(index, roots.Count);
            roots.Insert(idx, id);
        }

        return id;
    }

    /// <summary>Remove a node and all its descendants from the tree.
    /// Clears the active layer's focus if it was inside.</summary>
    public void Remove(NodeId id)
    {
        var doomed = new List<NodeId>();
        CollectSubtree(id, doomed);

        // Detach from the parent's children or the active layer's roots.
        if (_nodes.TryGetValue(id, out var node))
        {
            if (!node.Parent.IsNone)
            {
                if (_nodes.TryGetValue(node.Parent, out var parent))
                    parent.Children.Remove(id);
            }
            else
            {
                ActiveLayer.Roots.Remove(id);
            }
        }

        var layer = ActiveLayer;
        foreach (var nid in doomed)
        {
            if (layer.Focus == nid)
                layer.Focus = NodeId.None;
            _nodes.Remove(nid);
        }
    }

    private void CollectSubtree(NodeId id, List<NodeId> output)
    {
        output.Add(id);
        if (_nodes.TryGetValue(id, out var node))
            foreach (var child in node.Children)
                CollectSubtree(child, output);
    }

    /// <summary>Remove a subtree's nodes without touching any layer's
    /// roots list (used when popping a layer).</summary>
    private void RemoveSubtreeNodes(NodeId id)
    {
        var doomed = new List<NodeId>();
        CollectSubtree(id, doomed);
        foreach (var nid in doomed)
            _nodes.Remove(nid);
    }

    public Node? Get(NodeId id) => _nodes.GetValueOrDefault(id);

    public List<NodeId> Children(NodeId id) =>
        _nodes.TryGetValue(id, out var node) ? node.Children : EmptyChildren;

    private static readonly List<NodeId> EmptyChildren = new();

    /// <summary>None for a layer root or a missing node.</summary>
    public NodeId Parent(NodeId id) =>
        _nodes.TryGetValue(id, out var node) ? node.Parent : NodeId.None;

    public NodeId Focus => ActiveLayer.Focus;

    public void SetFocus(NodeId id) => ActiveLayer.Focus = id;

    public void ClearFocus() => ActiveLayer.Focus = NodeId.None;

    public IReadOnlyList<NodeId> Roots => ActiveLayer.Roots;

    public int Count => _nodes.Count;

    public bool Contains(NodeId id) => _nodes.ContainsKey(id);

    // ── Layer stack ──

    /// <summary>Push an empty layer; new root nodes go into it.</summary>
    public void PushLayer() => _layers.Add(new Layer());

    /// <summary>Pop the top layer, removing all its nodes. Returns the
    /// restored (now-active) layer's focus. Throws on the base layer.</summary>
    public NodeId PopLayer()
    {
        if (_layers.Count <= 1)
            throw new InvalidOperationException("Cannot pop the base layer");
        var layer = _layers[^1];
        _layers.RemoveAt(_layers.Count - 1);
        foreach (var root in layer.Roots)
            RemoveSubtreeNodes(root);
        return ActiveLayer.Focus;
    }

    public int LayerDepth => _layers.Count;

    // ── Primary / cancel ──

    /// <summary>The active layer's primary widget (Enter activates it).</summary>
    public NodeId Primary => ActiveLayer.Primary;

    public void SetPrimary(NodeId id) => ActiveLayer.Primary = id;

    /// <summary>The active layer's cancel widget (Escape activates it).</summary>
    public NodeId Cancel => ActiveLayer.Cancel;

    public void SetCancel(NodeId id) => ActiveLayer.Cancel = id;

    private Layer ActiveLayer => _layers[^1];
}
