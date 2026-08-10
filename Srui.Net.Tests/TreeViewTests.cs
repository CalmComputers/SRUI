using Srui;
using Xunit;

namespace Srui.Net.Tests;

/// <summary>TreeView behavior: branch-local navigation with wrap,
/// expand/collapse on the horizontal axis with left-to-parent
/// recovery, outward-scanning typeahead with collapsed-branch
/// fallback, and the programmatic surface (SetRoots, SelectNode,
/// Refresh).</summary>
public class TreeViewTests
{
    /// <summary>Two branches and a stray leaf:
    /// Vanilla [Joker, Blueprint], Extra Credit [Turtle, Joker],
    /// Hand size. The duplicate "Joker" is deliberate — proximity
    /// disambiguation is the typeahead contract.</summary>
    private static (TestUi Ui, TreeView Tree, TreeNode Vanilla, TreeNode Extra, TreeNode Leaf) Build(
        bool numbered = false, bool activateItems = false)
    {
        var ui = new TestUi();
        var vanilla = new TreeNode("Vanilla", new TreeNode("Joker"), new TreeNode("Blueprint"));
        var extra = new TreeNode("Extra Credit", new TreeNode("Turtle"), new TreeNode("Joker"));
        var leaf = new TreeNode("Hand size");
        var tree = new TreeView(ui.App, "Content", [vanilla, extra, leaf],
            numbered: numbered, activateItems: activateItems);
        tree.Focus();
        ui.Drain();
        return (ui, tree, vanilla, extra, leaf);
    }

    [Fact]
    public void DownMovesAmongSiblingsAndWrapsWithinTheBranch()
    {
        var (ui, tree, _, extra, leaf) = Build();

        ui.Input(InputKind.MoveDown);
        Assert.Same(extra, tree.SelectedNode);
        Assert.Equal(new[] { "Extra Credit, collapsed, 2 items" }, ui.Spoken());

        ui.Input(InputKind.MoveDown);
        Assert.Same(leaf, tree.SelectedNode);
        Assert.Equal(new[] { "Hand size" }, ui.Spoken());

        // Past the last root: wrap to the first, boundary marked.
        ui.Input(InputKind.MoveDown);
        Assert.Equal(new[] { "bottom, Vanilla, collapsed, 2 items" }, ui.Spoken());
    }

    [Fact]
    public void UpWrapsToTheLastSibling()
    {
        var (ui, tree, _, _, leaf) = Build();
        ui.Input(InputKind.MoveUp);
        Assert.Same(leaf, tree.SelectedNode);
        Assert.Equal(new[] { "top, Hand size" }, ui.Spoken());
    }

    [Fact]
    public void RightExpandsThenEnters()
    {
        var (ui, tree, vanilla, _, _) = Build();

        ui.Input(InputKind.MoveRight);
        Assert.True(vanilla.Expanded);
        Assert.Same(vanilla, tree.SelectedNode);            // expanding does not move
        Assert.Equal(new[] { "Vanilla, expanded, 2 items" }, ui.Spoken());

        ui.Input(InputKind.MoveRight);
        Assert.Equal("Joker", tree.SelectedNode!.Text);     // entering does
        Assert.Equal(new[] { "Joker" }, ui.Spoken());
    }

    [Fact]
    public void WrapIsBranchLocalInsideAnOpenBranch()
    {
        var (ui, tree, _, _, _) = Build();
        ui.Input(InputKind.MoveRight);                       // expand Vanilla
        ui.Input(InputKind.MoveRight);                       // enter: Joker
        ui.Input(InputKind.MoveDown);                        // Blueprint
        ui.Drain();

        // The branch is a room: down from its last child wraps to its
        // first, never to Extra Credit outside.
        ui.Input(InputKind.MoveDown);
        Assert.Equal("Joker", tree.SelectedNode!.Text);
        Assert.Equal(new[] { "bottom, Joker" }, ui.Spoken());
    }

    [Fact]
    public void LeftCollapsesThenJumpsToParent()
    {
        var (ui, tree, vanilla, _, _) = Build();
        ui.Input(InputKind.MoveRight);                       // expand
        ui.Input(InputKind.MoveRight);                       // enter: Joker
        ui.Drain();

        // On a leaf, left is the recovery move: up to the parent.
        ui.Input(InputKind.MoveLeft);
        Assert.Same(vanilla, tree.SelectedNode);
        Assert.Equal(new[] { "Vanilla, expanded, 2 items" }, ui.Spoken());

        // On an open branch, left closes it first...
        ui.Input(InputKind.MoveLeft);
        Assert.False(vanilla.Expanded);
        Assert.Equal(new[] { "Vanilla, collapsed, 2 items" }, ui.Spoken());

        // ...and at root level with nothing to close, it stays put.
        ui.Input(InputKind.MoveLeft);
        Assert.Same(vanilla, tree.SelectedNode);
        Assert.Equal(new[] { "Vanilla, collapsed, 2 items" }, ui.Spoken());
    }

    [Fact]
    public void HomeAndEndJumpWithinTheSiblings()
    {
        var (ui, tree, vanilla, _, leaf) = Build();
        ui.Input(InputKind.MoveToDocEnd);
        Assert.Same(leaf, tree.SelectedNode);
        ui.Input(InputKind.MoveToDocStart);
        Assert.Same(vanilla, tree.SelectedNode);
    }

    [Fact]
    public void NumberedPositionsCountSiblingsNotTheWholeTree()
    {
        var (ui, _, _, _, _) = Build(numbered: true);
        ui.Input(InputKind.MoveRight);                       // expand Vanilla
        ui.Drain();
        ui.Input(InputKind.MoveRight);                       // enter: Joker
        Assert.Equal(new[] { "Joker 1 of 2" }, ui.Spoken());
    }

    [Fact]
    public void TypeaheadFindsTheTopologicallyNearestBearer()
    {
        var (ui, tree, vanilla, extra, _) = Build();
        vanilla.Expanded = true;
        extra.Expanded = true;
        // Stand inside Extra Credit: its own Joker outranks Vanilla's.
        tree.SelectNode(extra.Children[0]);                  // Turtle
        ui.Drain();

        ui.Type('j');
        Assert.Same(extra.Children[1], tree.SelectedNode);
        Assert.Equal(new[] { "Joker" }, ui.Spoken());
    }

    [Fact]
    public void TypeaheadRevealsCollapsedContentWhenNothingVisibleMatches()
    {
        var (ui, tree, vanilla, _, _) = Build();
        Assert.False(vanilla.Expanded);

        ui.Type('b');                                        // Blueprint, hidden in Vanilla
        Assert.Equal("Blueprint", tree.SelectedNode!.Text);
        Assert.True(vanilla.Expanded);                       // landing revealed it
        Assert.Equal(new[] { "Blueprint" }, ui.Spoken());
    }

    [Fact]
    public void TypeaheadPrefersVisibleOverCollapsedMatches()
    {
        var (ui, tree, vanilla, extra, _) = Build();
        extra.Expanded = true;
        tree.SelectNode(extra.Children[0]);                  // Turtle
        ui.Drain();

        // 'j' matches Extra Credit's visible Joker and Vanilla's hidden
        // one; visible wins even though the hidden bearer exists.
        ui.Type('j');
        Assert.Same(extra.Children[1], tree.SelectedNode);
        Assert.False(vanilla.Expanded);
    }

    [Fact]
    public void MultiLetterPrefixSearchesFromTheCursorOut()
    {
        var (ui, tree, _, _, leaf) = Build();
        ui.Type('h');
        Assert.Same(leaf, tree.SelectedNode);
        Assert.Equal(new[] { "Hand size" }, ui.Spoken());

        // Still within the timeout: the prefix extends and the cursor
        // itself keeps the match (the multi-letter convention), spoken
        // again.
        ui.Type('a');
        Assert.Same(leaf, tree.SelectedNode);
        Assert.Equal(new[] { "Hand size" }, ui.Spoken());
    }

    [Fact]
    public void EnterActivatesWhenOptedIn()
    {
        var (ui, tree, vanilla, _, _) = Build(activateItems: true);
        TreeNode? activated = null;
        tree.Activated += () => activated = tree.SelectedNode;
        ui.Input(InputKind.Activate);
        ui.Drain();
        Assert.Same(vanilla, activated);
    }

    [Fact]
    public void SelectNodeRevealsAndSpeaks()
    {
        var (ui, tree, vanilla, _, _) = Build();
        var blueprint = vanilla.Children[1];

        tree.SelectNode(blueprint);
        Assert.True(vanilla.Expanded);
        Assert.Same(blueprint, tree.SelectedNode);
        Assert.Equal(new[] { "Blueprint" }, ui.Spoken());
    }

    [Fact]
    public void SetRootsReplacesTheTree()
    {
        var (ui, tree, _, _, _) = Build();
        tree.SetRoots([new TreeNode("Alpha"), new TreeNode("Beta")]);
        Assert.Equal("Alpha", tree.SelectedNode!.Text);
        ui.Drain();

        ui.Input(InputKind.MoveDown);
        Assert.Equal(new[] { "Beta" }, ui.Spoken());
    }

    [Fact]
    public void RefreshKeepsTheCursorWhenItsNodeSurvives()
    {
        var (ui, tree, vanilla, extra, _) = Build();
        tree.SelectNode(extra);
        ui.Drain();

        vanilla.Children.Add(new TreeNode("Stencil"));
        tree.Refresh();
        Assert.Same(extra, tree.SelectedNode);
        Assert.Same(vanilla, vanilla.Children[^1].Parent);   // the new node is linked
    }

    [Fact]
    public void RefreshReseatsACursorWhoseNodeLeft()
    {
        var (ui, tree, vanilla, extra, _) = Build();
        tree.SelectNode(extra);
        ui.Drain();

        // Remove the branch under the cursor entirely.
        var roots = new List<TreeNode>(tree.Roots);
        roots.Remove(extra);
        tree.SetRoots(roots);
        Assert.Same(vanilla, tree.SelectedNode);
    }

    [Fact]
    public void AnEmptyTreeAnswersNavigationWithEmpty()
    {
        var ui = new TestUi();
        var tree = new TreeView(ui.App, "Nothing", Array.Empty<TreeNode>());
        tree.Focus();
        ui.Drain();

        ui.Input(InputKind.MoveDown);
        Assert.Equal(new[] { "empty" }, ui.Spoken());
        Assert.Null(tree.SelectedNode);
    }

    [Fact]
    public void NodeToggledReportsUserExpansionOnly()
    {
        var (ui, tree, vanilla, _, _) = Build();
        var toggles = new List<(TreeNode Node, bool Expanded)>();
        tree.NodeToggled += (node, expanded) => toggles.Add((node, expanded));

        ui.Input(InputKind.MoveRight);                       // user expands
        ui.Drain();
        Assert.Equal([(vanilla, true)], toggles);

        vanilla.Expanded = false;                            // programmatic: silent, unreported
        ui.Drain();
        Assert.Single(toggles);
    }
}
