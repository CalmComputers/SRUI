using Srui;
using Xunit;

namespace Srui.Tests;

/// <summary>TreeView behavior: branch-local navigation with silent wrap,
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
    private static (TestApp Ui, TreeView Tree, TreeNode Vanilla, TreeNode Extra, TreeNode Leaf) Build(
        bool numbered = false, bool activateItems = false)
    {
        var ui = new TestApp();
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
        Assert.Equal(new[] { "Extra Credit collapsed 2 items" }, ui.Spoken());

        ui.Input(InputKind.MoveDown);
        Assert.Same(leaf, tree.SelectedNode);
        Assert.Equal(new[] { "Hand size" }, ui.Spoken());

        // Past the last root: wrap to the first, no boundary words —
        // the landed line is the whole report, like any other move.
        ui.Input(InputKind.MoveDown);
        Assert.Equal(new[] { "Vanilla collapsed 2 items" }, ui.Spoken());
    }

    [Fact]
    public void UpWrapsToTheLastSibling()
    {
        var (ui, tree, _, _, leaf) = Build();
        ui.Input(InputKind.MoveUp);
        Assert.Same(leaf, tree.SelectedNode);
        Assert.Equal(new[] { "Hand size" }, ui.Spoken());
    }

    [Fact]
    public void RightOpensAndEntersInOneGesture()
    {
        var (ui, tree, vanilla, _, _) = Build();

        // You pressed right because you want in: the branch opens and
        // the first child speaks — landing inside IS the report.
        ui.Input(InputKind.MoveRight);
        Assert.True(vanilla.Expanded);
        Assert.Equal("Joker", tree.SelectedNode!.Text);
        Assert.Equal(new[] { "Joker" }, ui.Spoken());
    }

    [Fact]
    public void WrapIsBranchLocalInsideAnOpenBranch()
    {
        var (ui, tree, _, _, _) = Build();
        ui.Input(InputKind.MoveRight);                       // open Vanilla, land on Joker
        ui.Input(InputKind.MoveDown);                        // Blueprint

        // The branch is a room: down from its last child wraps to its
        // first, never to Extra Credit outside.
        ui.Input(InputKind.MoveDown);
        Assert.Equal("Joker", tree.SelectedNode!.Text);
        Assert.Equal(new[] { "Joker" }, ui.Spoken());
    }

    [Fact]
    public void LeftCollapsesThenJumpsToParent()
    {
        var (ui, tree, vanilla, _, _) = Build();
        ui.Input(InputKind.MoveRight);                       // open Vanilla, land on Joker

        // On a leaf, left is the recovery move: up to the parent.
        ui.Input(InputKind.MoveLeft);
        Assert.Same(vanilla, tree.SelectedNode);
        Assert.Equal(new[] { "Vanilla expanded 2 items" }, ui.Spoken());

        // On an open branch, left closes it first...
        ui.Input(InputKind.MoveLeft);
        Assert.False(vanilla.Expanded);
        Assert.Equal(new[] { "Vanilla collapsed 2 items" }, ui.Spoken());

        // ...and at root level with nothing to close, it stays put.
        ui.Input(InputKind.MoveLeft);
        Assert.Same(vanilla, tree.SelectedNode);
        Assert.Equal(new[] { "Vanilla collapsed 2 items" }, ui.Spoken());
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
        ui.Input(InputKind.MoveRight);                       // open Vanilla, land on Joker
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

        ui.Type('j');
        Assert.Same(extra.Children[1], tree.SelectedNode);
        Assert.Equal(new[] { "Joker" }, ui.Spoken());
    }

    [Fact]
    public void TypeaheadPrefersSiblingsOverANeighborsDeepContent()
    {
        var ui = new TestApp();
        // Mystic is open with "mult" inside; Madness is Mystic's
        // sibling. From Mystic, 'm' must find the sibling at your
        // level, not dive into the flat-order-nearer subtree.
        var mystic = new TreeNode("Mystic", new TreeNode("mult")) { Expanded = true };
        var madness = new TreeNode("Madness");
        var tree = new TreeView(ui.App, "Jokers", [mystic, madness]);
        tree.Focus();

        ui.Type('m');
        Assert.Same(madness, tree.SelectedNode);
        Assert.Equal(new[] { "Madness" }, ui.Spoken());

        // Repeats rotate in flat order, so every bearer gets a turn —
        // including the subtree the first press deliberately skipped.
        ui.Type('m');
        Assert.Same(mystic, tree.SelectedNode);
        ui.Type('m');
        Assert.Equal("mult", tree.SelectedNode!.Text);
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

    /// <summary>Two collapsed crates and a stray leaf, for the
    /// provisional-reveal contract: Crate [Apple], Basket [Apricot],
    /// Zed. The shared "ap" prefix lets a refinement travel from one
    /// crate's content to the other's.</summary>
    private static (TestApp Ui, TreeView Tree, TreeNode Crate, TreeNode Basket) BuildCrates()
    {
        var ui = new TestApp();
        var crate = new TreeNode("Crate", new TreeNode("Apple"));
        var basket = new TreeNode("Basket", new TreeNode("Apricot"));
        var tree = new TreeView(ui.App, "Pantry", [crate, basket, new TreeNode("Zed")]);
        tree.Focus();
        ui.Drain();
        return (ui, tree, crate, basket);
    }

    [Fact]
    public void TypeaheadRefinementClosesTheBranchItPassedThrough()
    {
        var (ui, tree, crate, basket) = BuildCrates();

        ui.Type('a');                                        // Apple, hidden in Crate
        Assert.Equal("Apple", tree.SelectedNode!.Text);
        Assert.True(crate.Expanded);

        // "apr" walks off Apple to Basket's Apricot: Apple was not
        // the goal, so neither was opening Crate — it closes again,
        // and Basket opens in its place.
        ui.Type('p');
        ui.Type('r');
        Assert.Equal("Apricot", tree.SelectedNode!.Text);
        Assert.False(crate.Expanded);
        Assert.True(basket.Expanded);
    }

    [Fact]
    public void TypeaheadCyclingClosesThePreviousBearersBranch()
    {
        var ui = new TestApp();
        var crate = new TreeNode("Crate", new TreeNode("Joker B"));
        var visible = new TreeNode("Joker A");
        var tree = new TreeView(ui.App, "Jokers", [visible, crate]);
        tree.Focus();

        ui.Type('j');                                        // only hidden Joker B matches
        Assert.Equal("Joker B", tree.SelectedNode!.Text);
        Assert.True(crate.Expanded);

        // Cycling on: Joker B was not the one, and its crate closes
        // behind the departure.
        ui.Type('j');
        Assert.Same(visible, tree.SelectedNode);
        Assert.False(crate.Expanded);
    }

    [Fact]
    public void EngagingAcceptsATypeaheadReveal()
    {
        var (ui, tree, crate, _) = BuildCrates();
        ui.Type('a');                                        // Apple, Crate opens
        ui.Input(InputKind.MoveUp);                          // engagement: this is the place

        // A fresh search leaving later finds the reveal accepted.
        ui.App.SetNow(1000);
        ui.Type('z');
        Assert.Equal("Zed", tree.SelectedNode!.Text);
        Assert.True(crate.Expanded);
    }

    [Fact]
    public void UserOpenedBranchesSurviveTypeaheadMovingOn()
    {
        var (ui, tree, crate, basket) = BuildCrates();
        crate.Expanded = true;                               // the user's own doing

        ui.Type('a');                                        // Apple, already visible
        ui.Type('p');
        ui.Type('r');                                        // Apricot, Basket opens
        Assert.True(crate.Expanded);
        Assert.True(basket.Expanded);

        // A new search moving on closes only the typeahead's opening.
        ui.App.SetNow(1000);
        ui.Type('z');
        Assert.True(crate.Expanded);
        Assert.False(basket.Expanded);
    }

    [Fact]
    public void RefinementWithinABranchKeepsItProvisional()
    {
        var ui = new TestApp();
        var crate = new TreeNode("Crate", new TreeNode("Apple"), new TreeNode("Apricot"));
        var tree = new TreeView(ui.App, "Pantry", [crate, new TreeNode("Zed")]);
        tree.Focus();

        // "a" lands on hidden Apple; "apr" refines to its sibling —
        // the crate stays open, the landing merely moved rooms inside.
        ui.Type('a');
        ui.Type('p');
        ui.Type('r');
        Assert.Equal("Apricot", tree.SelectedNode!.Text);
        Assert.True(crate.Expanded);

        // But it never stopped being provisional: a fresh search
        // leaving the crate still closes it.
        ui.App.SetNow(1000);
        ui.Type('z');
        Assert.False(crate.Expanded);
    }

    [Fact]
    public void TypeaheadPrefersVisibleOverCollapsedMatches()
    {
        var (ui, tree, vanilla, extra, _) = Build();
        extra.Expanded = true;
        tree.SelectNode(extra.Children[0]);                  // Turtle

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
        var ui = new TestApp();
        var tree = new TreeView(ui.App, "Nothing", Array.Empty<TreeNode>());
        tree.Focus();

        ui.Input(InputKind.MoveDown);
        Assert.Equal(new[] { "empty" }, ui.Spoken());
        Assert.Null(tree.SelectedNode);
    }

    [Fact]
    public void MultiSelectChecksLeavesWithSpace()
    {
        var ui = new TestApp();
        var vanilla = new TreeNode("Vanilla", new TreeNode("Joker"), new TreeNode("Blueprint"));
        var tree = new TreeView(ui.App, "Content", [vanilla], multiSelect: true);
        tree.Focus();
        ui.Drain();

        var toggles = new List<(TreeNode Node, bool Checked)>();
        tree.NodeChecked += (node, on) => toggles.Add((node, on));

        // Space on a branch refuses with a word, changes nothing.
        ui.Type(' ');
        Assert.Equal(new[] { "Checks apply to items, not groups." }, ui.Spoken());
        Assert.Empty(tree.CheckedNodes);

        ui.Input(InputKind.MoveRight);                       // open, land on Joker
        ui.Type(' ');
        Assert.Equal(new[] { "checked" }, ui.Spoken());
        Assert.True(tree.IsChecked(vanilla.Children[0]));
        Assert.Equal([(vanilla.Children[0], true)], toggles);

        // Navigation speaks the checked state after the line.
        ui.Input(InputKind.MoveDown);
        Assert.Equal(new[] { "Blueprint" }, ui.Spoken());
        ui.Input(InputKind.MoveDown);                        // wrap back to Joker
        Assert.Equal(new[] { "Joker checked" }, ui.Spoken());

        ui.Type(' ');
        Assert.Equal(new[] { "not checked" }, ui.Spoken());
        Assert.Empty(tree.CheckedNodes);
    }

    [Fact]
    public void CheckedNodesSurviveCollapseAndListInTreeOrder()
    {
        var ui = new TestApp();
        var vanilla = new TreeNode("Vanilla", new TreeNode("Joker"), new TreeNode("Blueprint"));
        var extra = new TreeNode("Extra", new TreeNode("Turtle"));
        var tree = new TreeView(ui.App, "Content", [vanilla, extra], multiSelect: true);
        tree.Focus();
        ui.Drain();

        tree.SetChecked(extra.Children[0], true);
        tree.SetChecked(vanilla.Children[1], true);
        vanilla.Expanded = false;                            // hide one checked leaf
        Assert.Equal(
            new[] { vanilla.Children[1], extra.Children[0] },
            tree.CheckedNodes);                              // tree order, collapse ignored
    }

    [Fact]
    public void CheckableOverridesTheDefaultRule()
    {
        var ui = new TestApp();
        // A configurable item: the branch itself checks (include me),
        // one child checks (a property), one is activation-only.
        var joker = new TreeNode("Joker",
            new TreeNode("Foil"),
            new TreeNode("payout") { Checkable = false })
        { Checkable = true };
        var tree = new TreeView(ui.App, "Config", [joker], multiSelect: true);
        tree.Focus();

        ui.Type(' ');
        Assert.True(tree.IsChecked(joker));                  // branch opted in
        Assert.Equal(new[] { "checked" }, ui.Spoken());

        ui.Input(InputKind.MoveRight);                       // open, land on Foil
        ui.Input(InputKind.MoveDown);                        // payout
        ui.Type(' ');
        Assert.False(tree.IsChecked(joker.Children[1]));     // leaf opted out
        Assert.Equal(new[] { "Checks apply to items, not groups." }, ui.Spoken());
    }

    [Fact]
    public void NodeToggledReportsUserExpansionOnly()
    {
        var (ui, tree, vanilla, _, _) = Build();
        var toggles = new List<(TreeNode Node, bool Expanded)>();
        tree.NodeToggled += (node, expanded) => toggles.Add((node, expanded));

        ui.Input(InputKind.MoveRight);                       // user expands
        Assert.Equal([(vanilla, true)], toggles);

        vanilla.Expanded = false;                            // programmatic: silent, unreported
        ui.Drain();
        Assert.Single(toggles);
    }
}
