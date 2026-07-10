using Srui;
using Srui.Core;
using Xunit;

namespace Srui.Net.Tests;

public class NavTests
{
    private static WidgetLabel MakeLabel(string name, string roleText = "button", bool focusable = true) =>
        new(name, roleText) { Focusable = focusable };

    private static (Tree Tree, NodeId[] Ids) BuildDemoTree()
    {
        // Save (button), Options (group) -> [WordWrap (checkbox), Files (listbox)], Notes (editbox)
        var tree = new Tree();
        var save = tree.Insert(NodeId.None, 0, MakeLabel("Save"));
        var options = tree.Insert(NodeId.None, 1, MakeLabel("Options", "group", focusable: false));
        var wrap = tree.Insert(options, 0, MakeLabel("Word Wrap", "check box"));
        var files = tree.Insert(options, 1, MakeLabel("Recent Files", "list"));
        var notes = tree.Insert(NodeId.None, 2, MakeLabel("Notes", "edit"));
        return (tree, new[] { save, options, wrap, files, notes });
    }

    [Fact]
    public void TabNextCyclesThroughFocusable()
    {
        var (tree, ids) = BuildDemoTree();
        var (save, wrap, files, notes) = (ids[0], ids[2], ids[3], ids[4]);

        Assert.Equal(save, Nav.TabNext(tree, NodeId.None));
        Assert.Equal(wrap, Nav.TabNext(tree, save)); // skips Options group
        Assert.Equal(files, Nav.TabNext(tree, wrap));
        Assert.Equal(notes, Nav.TabNext(tree, files));
        Assert.Equal(save, Nav.TabNext(tree, notes)); // wraps
    }

    [Fact]
    public void TabPrevCyclesReverse()
    {
        var (tree, ids) = BuildDemoTree();
        var (save, wrap, files, notes) = (ids[0], ids[2], ids[3], ids[4]);

        Assert.Equal(notes, Nav.TabPrev(tree, save));
        Assert.Equal(save, Nav.TabPrev(tree, wrap));
        Assert.Equal(wrap, Nav.TabPrev(tree, files));
        Assert.Equal(files, Nav.TabPrev(tree, notes));
    }

    [Fact]
    public void TabEmptyTree()
    {
        var tree = new Tree();
        Assert.Equal(NodeId.None, Nav.TabNext(tree, NodeId.None));
        Assert.Equal(NodeId.None, Nav.TabPrev(tree, NodeId.None));
    }

    [Fact]
    public void TreeNavDownIntoGroup()
    {
        var (tree, ids) = BuildDemoTree();
        Assert.Equal(ids[2], Nav.TreeNav(tree, ids[1], TreeDirection.Down));
    }

    [Fact]
    public void TreeNavUpToParent()
    {
        var (tree, ids) = BuildDemoTree();
        Assert.Equal(ids[1], Nav.TreeNav(tree, ids[2], TreeDirection.Up));
    }

    [Fact]
    public void TreeNavSiblingsWrap()
    {
        var (tree, ids) = BuildDemoTree();
        var (wrap, files) = (ids[2], ids[3]);

        Assert.Equal(files, Nav.TreeNav(tree, wrap, TreeDirection.Right));
        Assert.Equal(wrap, Nav.TreeNav(tree, files, TreeDirection.Right)); // wraps
        Assert.Equal(files, Nav.TreeNav(tree, wrap, TreeDirection.Left)); // wraps
    }

    [Fact]
    public void TreeNavRootSiblings()
    {
        var (tree, ids) = BuildDemoTree();
        Assert.Equal(ids[1], Nav.TreeNav(tree, ids[0], TreeDirection.Right));
        Assert.Equal(ids[0], Nav.TreeNav(tree, ids[4], TreeDirection.Right));
    }

    [Fact]
    public void HiddenSubtreeSkippedByTab()
    {
        var tree = new Tree();
        var a = tree.Insert(NodeId.None, 0, MakeLabel("A"));
        var g = tree.Insert(NodeId.None, 1, MakeLabel("G", "group", focusable: false));
        var b = tree.Insert(g, 0, MakeLabel("B"));
        var c = tree.Insert(NodeId.None, 2, MakeLabel("C"));

        tree.Get(g)!.Label.States |= WidgetStates.Hidden;

        Assert.Equal(a, Nav.TabNext(tree, NodeId.None));
        Assert.Equal(c, Nav.TabNext(tree, a));
        Assert.Equal(a, Nav.TabNext(tree, c));
        // B is inside a hidden group, never reachable.
        Assert.Equal(a, Nav.TabNext(tree, b));
    }

    [Fact]
    public void HiddenNodeSkippedByTab()
    {
        var tree = new Tree();
        var a = tree.Insert(NodeId.None, 0, MakeLabel("A"));
        var b = tree.Insert(NodeId.None, 1, MakeLabel("B"));
        var c = tree.Insert(NodeId.None, 2, MakeLabel("C"));

        tree.Get(b)!.Label.States |= WidgetStates.Hidden;

        Assert.Equal(c, Nav.TabNext(tree, a));
        Assert.Equal(a, Nav.TabNext(tree, c));
    }

    [Fact]
    public void TreeNavDownSkipsHiddenChild()
    {
        var tree = new Tree();
        var g = tree.Insert(NodeId.None, 0, MakeLabel("G", "group", focusable: false));
        var hidden = tree.Insert(g, 0, MakeLabel("H"));
        var visible = tree.Insert(g, 1, MakeLabel("V"));

        tree.Get(hidden)!.Label.States |= WidgetStates.Hidden;

        Assert.Equal(visible, Nav.TreeNav(tree, g, TreeDirection.Down));
    }

    [Fact]
    public void SiblingNavSkipsHidden()
    {
        var tree = new Tree();
        var a = tree.Insert(NodeId.None, 0, MakeLabel("A"));
        var b = tree.Insert(NodeId.None, 1, MakeLabel("B"));
        var c = tree.Insert(NodeId.None, 2, MakeLabel("C"));

        tree.Get(b)!.Label.States |= WidgetStates.Hidden;

        Assert.Equal(c, Nav.TreeNav(tree, a, TreeDirection.Right));
        Assert.Equal(a, Nav.TreeNav(tree, c, TreeDirection.Left));
    }

    [Fact]
    public void RecoverFocusAfterRemoval()
    {
        var (tree, ids) = BuildDemoTree();
        var (save, options, wrap, files) = (ids[0], ids[1], ids[2], ids[3]);

        tree.SetFocus(wrap);
        tree.Remove(wrap);

        var recovered = Nav.RecoverFocus(tree, options);
        Assert.NotEqual(NodeId.None, recovered);
        Assert.True(recovered == files || recovered == save);
    }

    // ── Property tests (ported from proptest, seeded random) ──

    private static (string RoleText, bool Focusable) RandomRole(Random rng) => rng.Next(7) switch
    {
        0 => ("button", true),
        1 => ("check box", true),
        2 => ("edit", true),
        3 => ("list", true),
        4 => ("group", false),
        5 => ("label", false),
        _ => ("tab control", true),
    };

    private static WidgetStates RandomStates(Random rng) =>
        (WidgetStates)(uint)rng.Next(64)
        & (WidgetStates.Disabled | WidgetStates.Required | WidgetStates.Warning | WidgetStates.Hidden);

    private static Tree BuildRandomTree(Random rng)
    {
        var tree = new Tree();
        var rootCount = rng.Next(1, 10);
        for (var i = 0; i < rootCount; i++)
        {
            var (roleText, focusable) = RandomRole(rng);
            var label = new WidgetLabel($"node_{i}", roleText)
            {
                Focusable = focusable,
                States = RandomStates(rng),
            };
            var id = tree.Insert(NodeId.None, i, label);
            var children = rng.Next(4);
            for (var c = 0; c < children; c++)
            {
                var childRole = c % 2 == 0 ? "button" : "check box";
                tree.Insert(id, c, new WidgetLabel($"child_{i}_{c}", childRole));
            }
        }
        return tree;
    }

    [Fact]
    public void TabNextAlwaysLandsOnValidNode()
    {
        var rng = new Random(42);
        for (var iteration = 0; iteration < 200; iteration++)
        {
            var tree = BuildRandomTree(rng);
            var focusable = Nav.CollectFocusableDfs(tree);
            var current = NodeId.None;
            var tabCount = rng.Next(1, 30);
            for (var t = 0; t < tabCount; t++)
            {
                var next = Nav.TabNext(tree, current);
                if (focusable.Count == 0)
                {
                    Assert.Equal(NodeId.None, next);
                }
                else
                {
                    Assert.NotEqual(NodeId.None, next);
                    var node = tree.Get(next);
                    Assert.NotNull(node);
                    Assert.True(node!.Label.IsFocusableNow);
                }
                current = next;
            }
        }
    }

    [Fact]
    public void TabNextCyclesAllFocusableExactlyOnce()
    {
        var rng = new Random(1337);
        for (var iteration = 0; iteration < 200; iteration++)
        {
            var tree = BuildRandomTree(rng);
            var focusable = Nav.CollectFocusableDfs(tree);

            if (focusable.Count == 0)
            {
                Assert.Equal(NodeId.None, Nav.TabNext(tree, NodeId.None));
                continue;
            }

            var visited = new List<NodeId>(focusable.Count);
            var current = NodeId.None;
            for (var i = 0; i < focusable.Count; i++)
            {
                current = Nav.TabNext(tree, current);
                Assert.DoesNotContain(current, visited);
                visited.Add(current);
            }
            foreach (var id in focusable)
                Assert.Contains(id, visited);

            // The (N+1)th tab wraps back to the first visited node.
            Assert.Equal(visited[0], Nav.TabNext(tree, current));
        }
    }
}

public class ReservedTests
{
    [Fact]
    public void TabAndShiftTabReserved()
    {
        Assert.NotNull(KeyCombo.Plain(Key.Tab).ReservedReason);
        Assert.NotNull(KeyCombo.WithShift(Key.Tab).ReservedReason);
    }

    [Fact]
    public void CtrlTabIsBindable()
    {
        Assert.Null(KeyCombo.WithCtrl(Key.Tab).ReservedReason);
        Assert.Null(KeyCombo.CtrlShift(Key.Tab).ReservedReason);
    }

    [Fact]
    public void AltLetterReservedButAltShiftLetterFree()
    {
        Assert.NotNull(KeyCombo.WithAlt(Key.Char('s')).ReservedReason);
        Assert.Null(KeyCombo.AltShift(Key.Char('s')).ReservedReason);
        Assert.Null(KeyCombo.WithAlt(Key.Char('5')).ReservedReason);
    }

    [Fact]
    public void AltArrowsReserved()
    {
        foreach (var key in new[] { Key.Up, Key.Down, Key.Left, Key.Right })
        {
            Assert.NotNull(KeyCombo.WithAlt(key).ReservedReason);
            Assert.Null(KeyCombo.Plain(key).ReservedReason);
            Assert.Null(KeyCombo.WithCtrl(key).ReservedReason);
        }
    }

    [Fact]
    public void OrdinaryCommandsAreBindable()
    {
        Assert.Null(KeyCombo.WithCtrl(Key.Char('s')).ReservedReason);
        Assert.Null(KeyCombo.Plain(Key.F(5)).ReservedReason);
        Assert.Null(KeyCombo.Plain(Key.Escape).ReservedReason);
    }
}
