using Srui;
using Srui.Core;
using Xunit;

namespace Srui.Net.Tests;

public class TreeTests
{
    private static WidgetLabel MakeLabel(string name, string roleText = "button", bool focusable = true) =>
        new(name, roleText) { Focusable = focusable };

    [Fact]
    public void InsertRootNodes()
    {
        var tree = new Tree();
        var a = tree.Insert(NodeId.None, 0, MakeLabel("A"));
        var b = tree.Insert(NodeId.None, 1, MakeLabel("B"));

        Assert.Equal(new[] { a, b }, tree.Roots);
        Assert.Equal(2, tree.Count);
        Assert.NotNull(tree.Get(a));
        Assert.NotNull(tree.Get(b));
    }

    [Fact]
    public void InsertChildren()
    {
        var tree = new Tree();
        var parent = tree.Insert(NodeId.None, 0, MakeLabel("Group", "group", focusable: false));
        var child1 = tree.Insert(parent, 0, MakeLabel("A"));
        var child2 = tree.Insert(parent, 1, MakeLabel("B"));

        Assert.Equal(new[] { child1, child2 }, tree.Children(parent));
        Assert.Equal(parent, tree.Parent(child1));
        Assert.Equal(parent, tree.Parent(child2));
    }

    [Fact]
    public void RemoveSubtree()
    {
        var tree = new Tree();
        var parent = tree.Insert(NodeId.None, 0, MakeLabel("Group", "group", focusable: false));
        var child = tree.Insert(parent, 0, MakeLabel("A"));
        var grandchild = tree.Insert(child, 0, MakeLabel("B"));

        tree.Remove(child);

        Assert.Equal(1, tree.Count);
        Assert.Null(tree.Get(child));
        Assert.Null(tree.Get(grandchild));
        Assert.Empty(tree.Children(parent));
    }

    [Fact]
    public void RemoveClearsFocus()
    {
        var tree = new Tree();
        var a = tree.Insert(NodeId.None, 0, MakeLabel("A"));
        tree.SetFocus(a);
        Assert.Equal(a, tree.Focus);

        tree.Remove(a);
        Assert.Equal(NodeId.None, tree.Focus);
    }

    [Fact]
    public void FocusLifecycle()
    {
        var tree = new Tree();
        Assert.Equal(NodeId.None, tree.Focus);

        var a = tree.Insert(NodeId.None, 0, MakeLabel("A"));
        tree.SetFocus(a);
        Assert.Equal(a, tree.Focus);

        tree.ClearFocus();
        Assert.Equal(NodeId.None, tree.Focus);
    }

    [Fact]
    public void StaleIdResolvesToNothing()
    {
        var tree = new Tree();
        var a = tree.Insert(NodeId.None, 0, MakeLabel("A"));
        tree.Remove(a);
        var b = tree.Insert(NodeId.None, 0, MakeLabel("B"));
        Assert.Null(tree.Get(a));
        Assert.NotNull(tree.Get(b));
    }

    // ── Layer stack ──

    [Fact]
    public void PushCreatesIsolatedRoots()
    {
        var tree = new Tree();
        var a = tree.Insert(NodeId.None, 0, MakeLabel("A"));
        tree.SetFocus(a);

        tree.PushLayer();
        Assert.Empty(tree.Roots);
        Assert.Equal(NodeId.None, tree.Focus);

        var b = tree.Insert(NodeId.None, 0, MakeLabel("B"));
        Assert.Equal(new[] { b }, tree.Roots);

        Assert.NotNull(tree.Get(a));
        Assert.NotNull(tree.Get(b));
    }

    [Fact]
    public void PopRemovesNodes()
    {
        var tree = new Tree();
        var a = tree.Insert(NodeId.None, 0, MakeLabel("A"));
        tree.SetFocus(a);

        tree.PushLayer();
        var b = tree.Insert(NodeId.None, 0, MakeLabel("B"));
        var c = tree.Insert(b, 0, MakeLabel("C"));

        Assert.Equal(3, tree.Count);

        tree.PopLayer();

        Assert.Null(tree.Get(b));
        Assert.Null(tree.Get(c));
        Assert.NotNull(tree.Get(a));
        Assert.Equal(1, tree.Count);
    }

    [Fact]
    public void PopRestoresFocus()
    {
        var tree = new Tree();
        var a = tree.Insert(NodeId.None, 0, MakeLabel("A"));
        tree.SetFocus(a);

        tree.PushLayer();
        var b = tree.Insert(NodeId.None, 0, MakeLabel("B"));
        tree.SetFocus(b);

        var restored = tree.PopLayer();
        Assert.Equal(a, restored);
        Assert.Equal(a, tree.Focus);
    }

    [Fact]
    public void CrossLayerGetWorks()
    {
        var tree = new Tree();
        var a = tree.Insert(NodeId.None, 0, MakeLabel("A"));

        tree.PushLayer();
        Assert.NotNull(tree.Get(a));
        Assert.Equal("A", tree.Get(a)!.Label.Name);
    }

    [Fact]
    public void CannotPopBaseLayer()
    {
        var tree = new Tree();
        Assert.Throws<InvalidOperationException>(() => tree.PopLayer());
    }

    [Fact]
    public void LayerDepth()
    {
        var tree = new Tree();
        Assert.Equal(1, tree.LayerDepth);

        tree.PushLayer();
        Assert.Equal(2, tree.LayerDepth);

        tree.PushLayer();
        Assert.Equal(3, tree.LayerDepth);

        tree.PopLayer();
        Assert.Equal(2, tree.LayerDepth);
    }
}

public class FocusMemoryTests
{
    [Fact]
    public void RememberAndRecall()
    {
        var tree = new Tree();
        var group = tree.Insert(NodeId.None, 0, new WidgetLabel("G", "group") { Focusable = false });
        var a = tree.Insert(group, 0, new WidgetLabel("A", "button"));
        var b = tree.Insert(group, 1, new WidgetLabel("B", "button"));

        var memory = new FocusMemory();
        Assert.Equal(NodeId.None, memory.Recall(group));

        memory.Remember(group, a);
        Assert.Equal(a, memory.Recall(group));

        memory.Remember(group, b);
        Assert.Equal(b, memory.Recall(group));
    }

    [Fact]
    public void GcDropsDeadEntries()
    {
        var tree = new Tree();
        var group = tree.Insert(NodeId.None, 0, new WidgetLabel("G", "group") { Focusable = false });
        var a = tree.Insert(group, 0, new WidgetLabel("A", "button"));

        var memory = new FocusMemory();
        memory.Remember(group, a);

        tree.Remove(a);
        memory.Gc(tree);
        Assert.Equal(NodeId.None, memory.Recall(group));
    }

    [Fact]
    public void GcDropsEntryWhenContainerRemoved()
    {
        var tree = new Tree();
        var group = tree.Insert(NodeId.None, 0, new WidgetLabel("G", "group") { Focusable = false });
        var a = tree.Insert(group, 0, new WidgetLabel("A", "button"));

        var memory = new FocusMemory();
        memory.Remember(group, a);

        tree.Remove(group);
        memory.Gc(tree);
        Assert.Equal(NodeId.None, memory.Recall(group));
    }
}
