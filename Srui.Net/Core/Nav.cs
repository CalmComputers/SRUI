namespace Srui.Core;

/// <summary>Direction for tree navigation (Alt+arrows).</summary>
internal enum TreeDirection
{
    /// <summary>Parent.</summary>
    Up,
    /// <summary>First non-hidden child.</summary>
    Down,
    /// <summary>Previous sibling (wraps).</summary>
    Left,
    /// <summary>Next sibling (wraps).</summary>
    Right,
}

/// <summary>Navigation algorithms — Tab, Shift+Tab, Alt+arrows, widget
/// shortcuts, focus recovery.</summary>
internal static class Nav
{
    /// <summary>Tab forward: depth-first traversal to the next focusable
    /// node, wrapping at the end. None when nothing is focusable.</summary>
    public static NodeId TabNext(Tree tree, NodeId current)
    {
        var all = CollectFocusableDfs(tree);
        if (all.Count == 0)
            return NodeId.None;

        if (current.IsNone)
            return all[0];
        var pos = all.IndexOf(current);
        return pos >= 0 ? all[(pos + 1) % all.Count] : all[0];
    }

    /// <summary>Tab backward: depth-first traversal to the previous
    /// focusable node, wrapping at the beginning.</summary>
    public static NodeId TabPrev(Tree tree, NodeId current)
    {
        var all = CollectFocusableDfs(tree);
        if (all.Count == 0)
            return NodeId.None;

        if (current.IsNone)
            return all[^1];
        var pos = all.IndexOf(current);
        return pos switch
        {
            0 => all[^1],
            > 0 => all[pos - 1],
            _ => all[^1],
        };
    }

    /// <summary>Alt+arrow navigation within the tree structure. None when
    /// there is nowhere to go.</summary>
    public static NodeId TreeNav(Tree tree, NodeId current, TreeDirection direction)
    {
        switch (direction)
        {
            case TreeDirection.Up:
                return tree.Parent(current);

            case TreeDirection.Down:
                foreach (var child in tree.Children(current))
                {
                    var node = tree.Get(child);
                    if (node is not null && (node.Label.States & WidgetStates.Hidden) == 0)
                        return child;
                }
                return NodeId.None;

            case TreeDirection.Left:
            {
                var siblings = SiblingList(tree, current);
                var pos = siblings.IndexOf(current);
                if (pos < 0 || siblings.Count <= 1)
                    return NodeId.None;
                return siblings[pos == 0 ? siblings.Count - 1 : pos - 1];
            }

            case TreeDirection.Right:
            {
                var siblings = SiblingList(tree, current);
                var pos = siblings.IndexOf(current);
                if (pos < 0 || siblings.Count <= 1)
                    return NodeId.None;
                return siblings[(pos + 1) % siblings.Count];
            }

            default:
                return NodeId.None;
        }
    }

    /// <summary>Find the first interactive (focusable, enabled, outside
    /// hidden subtrees) widget with a shortcut bound to the combo, and the
    /// action of its first matching binding. Depth-first from the roots,
    /// so match order follows insertion order. A disabled widget is
    /// tabbable but its shortcuts are inert.</summary>
    public static (NodeId Node, ShortcutAction Action)? FindShortcut(Tree tree, KeyCombo combo)
    {
        foreach (var id in CollectFocusableDfs(tree))
        {
            var node = tree.Get(id);
            if (node is null || !node.Label.IsInteractiveNow)
                continue;
            foreach (var shortcut in node.Label.Shortcuts)
                if (shortcut.Combo == combo)
                    return (id, shortcut.Action);
        }
        return null;
    }

    /// <summary>Recover focus after the focused node was removed: tab
    /// onward from the nearest surviving ancestor, else the first
    /// focusable root.</summary>
    public static NodeId RecoverFocus(Tree tree, NodeId removedParent)
    {
        if (!removedParent.IsNone && tree.Contains(removedParent))
        {
            var next = TabNext(tree, removedParent);
            if (!next.IsNone)
                return next;
        }
        return TabNext(tree, NodeId.None);
    }

    /// <summary>All focusable nodes in depth-first order, skipping entire
    /// hidden subtrees. Disabled widgets are included: only hiding takes a
    /// widget out of navigation.</summary>
    public static List<NodeId> CollectFocusableDfs(Tree tree)
    {
        var result = new List<NodeId>();
        foreach (var root in tree.Roots)
            CollectFocusableRecursive(tree, root, result);
        return result;
    }

    private static void CollectFocusableRecursive(Tree tree, NodeId id, List<NodeId> output)
    {
        var node = tree.Get(id);
        if (node is null)
            return;
        if ((node.Label.States & WidgetStates.Hidden) != 0)
            return;
        if (node.Label.IsFocusableNow)
            output.Add(id);
        foreach (var child in node.Children)
            CollectFocusableRecursive(tree, child, output);
    }

    /// <summary>The sibling list for a node (either roots or the parent's
    /// children), with hidden siblings filtered out.</summary>
    private static List<NodeId> SiblingList(Tree tree, NodeId id)
    {
        var parent = tree.Parent(id);
        IReadOnlyList<NodeId> raw = parent.IsNone ? tree.Roots : tree.Children(parent);
        var result = new List<NodeId>(raw.Count);
        foreach (var sibling in raw)
        {
            var node = tree.Get(sibling);
            if (node is not null && (node.Label.States & WidgetStates.Hidden) == 0)
                result.Add(sibling);
        }
        return result;
    }
}
