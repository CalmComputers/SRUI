namespace Srui.Core;

/// <summary>A single node in the semantic tree.</summary>
internal sealed class Node
{
    public readonly NodeId Id;
    /// <summary>None for a layer root.</summary>
    public NodeId Parent;
    public readonly List<NodeId> Children = new();
    public WidgetLabel Label;
    /// <summary>The widget object this node embodies: it handles the
    /// node's input and receives its events. Null only for nodes built
    /// below the class layer (engine tests). Dies with the node, so a
    /// queued event for a removed widget resolves to nothing.</summary>
    public readonly Widget? Owner;

    public Node(NodeId id, NodeId parent, WidgetLabel label, Widget? owner)
    {
        Id = id;
        Parent = parent;
        Label = label;
        Owner = owner;
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
    public NodeId Insert(NodeId parent, int index, WidgetLabel label, Widget? owner = null)
    {
        var id = new NodeId(_nextId++);
        _nodes[id] = new Node(id, parent, label, owner);

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

        // Detach from the parent's children or its own layer's roots -
        // a ground root removed from under a dialog leaves the ground's
        // list, not the dialog's.
        var layer = LayerOf(id);
        if (_nodes.TryGetValue(id, out var node))
        {
            if (!node.Parent.IsNone)
            {
                if (_nodes.TryGetValue(node.Parent, out var parent))
                    parent.Children.Remove(id);
            }
            else
            {
                layer.Roots.Remove(id);
            }
        }

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

    /// <summary>Set the focus of the layer <paramref name="id"/> lives
    /// in - the active layer for None or an unknown node. Focus,
    /// primary, and cancel are each a layer's own, so a write naming a
    /// node under a dialog lands in that node's layer, where the pop
    /// will find it, rather than in the dialog's, where the pop would
    /// discard it.</summary>
    public void SetFocus(NodeId id) => LayerOf(id).Focus = id;

    /// <summary>The focus of the layer <paramref name="id"/> lives in.</summary>
    public NodeId FocusInLayerOf(NodeId id) => LayerOf(id).Focus;

    /// <summary>Whether <paramref name="id"/> is a node of the active
    /// layer - reachable by the user now, layer-wise.</summary>
    public bool InActiveLayer(NodeId id) => ReferenceEquals(LayerOf(id), ActiveLayer);

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

    /// <summary>Set the primary of the layer <paramref name="id"/>
    /// lives in (see <see cref="SetFocus"/>).</summary>
    public void SetPrimary(NodeId id) => LayerOf(id).Primary = id;

    /// <summary>The active layer's cancel widget (Escape activates it).</summary>
    public NodeId Cancel => ActiveLayer.Cancel;

    /// <summary>Set the cancel of the layer <paramref name="id"/>
    /// lives in (see <see cref="SetFocus"/>).</summary>
    public void SetCancel(NodeId id) => LayerOf(id).Cancel = id;

    private Layer ActiveLayer => _layers[^1];

    /// <summary>The layer holding <paramref name="id"/>'s root ancestor;
    /// the active layer for None or a node the tree does not hold. A
    /// walk to the root and a scan of each layer's roots, top down -
    /// the common case, a node of the active layer, answers at the
    /// first layer.</summary>
    private Layer LayerOf(NodeId id)
    {
        if (id.IsNone || !_nodes.TryGetValue(id, out var node))
            return ActiveLayer;
        while (!node.Parent.IsNone && _nodes.TryGetValue(node.Parent, out var parent))
            node = parent;
        for (var i = _layers.Count - 1; i >= 0; i--)
            if (_layers[i].Roots.Contains(node.Id))
                return _layers[i];
        return ActiveLayer;
    }
}
