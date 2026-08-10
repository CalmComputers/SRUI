using Srui.Core;

namespace Srui;

/// <summary>A node a <see cref="TreeView{T}"/> holds: a spoken line,
/// children, and an expansion flag. State-bearing application node
/// types derive from the self-typed form (<c>class StatNode :
/// TreeNode&lt;StatNode&gt;</c>) so traversal hands them back without
/// casts; plain text rides the sealed <see cref="TreeNode"/>. The
/// widget stamps <see cref="Parent"/> links when it takes the roots
/// (and on <see cref="TreeView{T}.Refresh"/> after structural
/// mutation); children lists are the application's to build.</summary>
public class TreeNode<T> : IListItem where T : TreeNode<T>
{
    public TreeNode(string text) => Text = text;

    /// <summary>The node's line: spoken by readers, matched by
    /// typeahead. Read live at announcement time, like every list
    /// item's.</summary>
    public string Text { get; set; }

    /// <summary>The node's children, in display order. Mutating the
    /// structure under a live widget needs a <see
    /// cref="TreeView{T}.Refresh"/> so parent links and the cursor
    /// keep up.</summary>
    public List<T> Children { get; } = [];

    /// <summary>Whether the branch is open. Meaningless on a leaf.
    /// Programmatic writes are silent; the widget speaks only
    /// user-driven expansion.</summary>
    public bool Expanded { get; set; }

    /// <summary>The owning node, stamped by the widget — null at root
    /// level.</summary>
    public T? Parent { get; internal set; }

    /// <summary>Whether this node has children.</summary>
    public bool IsBranch => Children.Count > 0;
}

/// <summary>The plain-text node, for trees whose nodes carry no
/// application state.</summary>
public sealed class TreeNode : TreeNode<TreeNode>
{
    public TreeNode(string text, params TreeNode[] children) : base(text)
        => Children.AddRange(children);
}

/// <summary>Single-selection tree over typed nodes: a list whose items
/// expand. Navigation is branch-local — Up/Down move among the current
/// node's siblings and wrap within them (the wrap carries a boundary
/// marker, so readers can cue the seam while the landed item speaks),
/// making a branch a room rather than a region of one long hallway.
/// Right expands a closed branch, then steps into its first child;
/// Left collapses an open branch, then jumps to the parent — the
/// "where am I" recovery move. Home/End jump within the siblings.
///
/// Typeahead searches outward from the cursor — nearer nodes in the
/// flattened visible order match first, so a name that repeats across
/// branches resolves to the topologically closest bearer — and then
/// falls back to nodes inside collapsed branches, outward again;
/// landing on one reveals it (its ancestors expand). Single letters
/// cycle like a list's; multi-letter prefixes search from the cursor
/// out.
///
/// Enter is not claimed by default (the dialog convention — it falls
/// through to the layer's primary); <c>activateItems: true</c> claims
/// it and raises <see cref="Widget.Activated"/> for the selected
/// node.</summary>
public class TreeView<T> : Widget where T : TreeNode<T>
{
    /// <summary>Timeout for resetting the typeahead buffer (milliseconds
    /// of host time).</summary>
    private const ulong TypeAheadTimeoutMs = 400;

    private List<T> _roots;
    private T? _cursor;
    private readonly bool _numbered;
    private readonly bool _activateItems;
    private string _typeAheadBuffer = "";
    private ulong? _lastKeystrokeMs;

    public TreeView(
        IWidgetContainer parent, string name, IReadOnlyList<T> roots,
        bool numbered = false, bool activateItems = false)
        : base(parent, name, "tree view")
    {
        _roots = new List<T>(roots);
        StampParents(_roots, null);
        _cursor = _roots.Count > 0 ? _roots[0] : null;
        _numbered = numbered;
        _activateItems = activateItems;
    }

    /// <summary>The root nodes. Setting replaces the tree (parent
    /// links stamped, cursor moved to the first root) and speaks the
    /// newly selected node when focused.</summary>
    public IReadOnlyList<T> Roots
    {
        get => _roots;
        set => SetRoots(value);
    }

    /// <summary>Replace the tree; equivalent to setting
    /// <see cref="Roots"/>.</summary>
    public void SetRoots(IReadOnlyList<T> roots)
    {
        var copy = new List<T>(roots);
        Engine.UpdateLabel(Node, _ =>
        {
            _roots = copy;
            StampParents(_roots, null);
            _cursor = _roots.Count > 0 ? _roots[0] : null;
        });
    }

    /// <summary>Re-stamp parent links and re-seat the cursor after the
    /// application mutated node children in place. The cursor keeps
    /// its node when the node is still in the tree, else it falls to
    /// the first root. Silent — the caller speaks any delta the user
    /// should hear.</summary>
    public void Refresh()
    {
        StampParents(_roots, null);
        if (_cursor is null || !InTree(_cursor))
            _cursor = _roots.Count > 0 ? _roots[0] : null;
    }

    /// <summary>The selected node, or null when the tree is empty.</summary>
    public T? SelectedNode => _cursor;

    /// <summary>Move the selection to a node in the tree, revealing it
    /// (ancestors expand). A move while focused speaks the node
    /// exactly as a user-driven move would.</summary>
    public void SelectNode(T node)
    {
        if (!InTree(node))
            throw new ArgumentException("the node is not in this tree", nameof(node));
        Reveal(node);
        if (ReferenceEquals(node, _cursor))
            return;
        _cursor = node;
        if (IsFocused)
            AnnounceCursor(null);
    }

    /// <summary>The selected node's line (or "empty"), pulled fresh at
    /// announcement time.</summary>
    protected internal override string ValueText =>
        _cursor is { } c ? NodeLine(c) : "empty";

    /// <summary>"N of M" among the selected node's siblings, when
    /// numbered.</summary>
    protected internal override string StateText
    {
        get
        {
            if (!_numbered || _cursor is not { } c)
                return "";
            var siblings = SiblingsOf(c);
            return $"{siblings.IndexOf(c) + 1} of {siblings.Count}";
        }
    }

    /// <summary>The user expanded or collapsed a branch; the arguments
    /// are the node and its new state.</summary>
    public event Action<T, bool>? NodeToggled;

    protected virtual void OnNodeToggled(T node, bool expanded) =>
        NodeToggled?.Invoke(node, expanded);

    // ── Structure helpers ──

    private static void StampParents(List<T> nodes, T? parent)
    {
        foreach (var node in nodes)
        {
            node.Parent = parent;
            StampParents(node.Children, node);
        }
    }

    private List<T> SiblingsOf(T node) => node.Parent?.Children ?? _roots;

    private bool InTree(T node)
    {
        var top = node;
        while (top.Parent is { } p)
            top = p;
        return _roots.Contains(top) && Lineage(node);

        bool Lineage(T n) => n.Parent is not { } p || (p.Children.Contains(n) && Lineage(p));
    }

    private static void Reveal(T node)
    {
        for (var p = node.Parent; p is not null; p = p.Parent)
            p.Expanded = true;
    }

    /// <summary>Every node whose line is currently reachable without
    /// expanding anything — roots and the descendants of open
    /// branches, in tree order.</summary>
    private List<T> VisibleNodes()
    {
        var result = new List<T>();
        void Walk(List<T> nodes)
        {
            foreach (var node in nodes)
            {
                result.Add(node);
                if (node.Expanded)
                    Walk(node.Children);
            }
        }
        Walk(_roots);
        return result;
    }

    /// <summary>Every node, in tree order, expansion ignored.</summary>
    private List<T> AllNodes()
    {
        var result = new List<T>();
        void Walk(List<T> nodes)
        {
            foreach (var node in nodes)
            {
                result.Add(node);
                Walk(node.Children);
            }
        }
        Walk(_roots);
        return result;
    }

    // ── Announcement ──

    /// <summary>A node's spoken line: the text, plus the expansion
    /// state and child count for branches — leaves are their text
    /// alone.</summary>
    private static string NodeLine(T node) =>
        node.IsBranch
            ? $"{node.Text}, {(node.Expanded ? "expanded" : "collapsed")}, {node.Children.Count} items"
            : node.Text;

    private void AnnounceCursor(Boundary? boundary)
    {
        if (_cursor is not { } c)
            return;
        (int, int)? position = null;
        if (_numbered)
        {
            var siblings = SiblingsOf(c);
            position = (siblings.IndexOf(c), siblings.Count);
        }
        AnnounceItem(NodeLine(c), position, boundary);
    }

    private void AnnounceEmpty() => AnnounceItem("empty", null, null);

    private void MoveAndAnnounce(T node, Boundary? boundary = null)
    {
        _cursor = node;
        AnnounceCursor(boundary);
        PostChanged();
    }

    // ── Typeahead ──

    /// <summary>Candidates in match priority order: visible nodes
    /// scanning outward from the cursor (nearest first, alternating
    /// after/before), then hidden nodes the same way — collapsed
    /// content matches only when nothing reachable does. <paramref
    /// name="includeCursor"/> puts the cursor itself first (the
    /// multi-letter prefix convention; single-letter cycling starts
    /// past it).</summary>
    private List<T> TypeaheadCandidates(bool includeCursor)
    {
        var visible = VisibleNodes();
        var result = new List<T>();
        if (_cursor is not { } cursor)
            return result;
        int ci = visible.IndexOf(cursor);
        if (includeCursor)
            result.Add(cursor);
        for (int d = 1; d < visible.Count; d++)
        {
            if (ci + d < visible.Count) result.Add(visible[ci + d]);
            if (ci - d >= 0) result.Add(visible[ci - d]);
        }

        var all = AllNodes();
        var visibleSet = new HashSet<T>(visible, ReferenceEqualityComparer.Instance);
        var hidden = new List<(T Node, int Index)>();
        for (int i = 0; i < all.Count; i++)
            if (!visibleSet.Contains(all[i]))
                hidden.Add((all[i], i));
        int cursorAt = all.IndexOf(cursor);
        hidden.Sort((a, b) =>
        {
            int byDistance = Math.Abs(a.Index - cursorAt).CompareTo(Math.Abs(b.Index - cursorAt));
            return byDistance != 0 ? byDistance : a.Index.CompareTo(b.Index);
        });
        result.AddRange(hidden.Select(h => h.Node));
        return result;
    }

    private void HandleTypeAhead(string runeText)
    {
        var runeLower = AsciiMatch.LowerString(runeText);

        var now = NowMs;
        var shouldReset = _lastKeystrokeMs is not ulong last
            || now - Math.Min(now, last) > TypeAheadTimeoutMs;
        var cycling = _typeAheadBuffer.Length > 0 && AsciiMatch.IsRepeatsOf(_typeAheadBuffer, runeLower);

        if (shouldReset || cycling)
            _typeAheadBuffer = "";
        _typeAheadBuffer += runeLower;
        _lastKeystrokeMs = now;

        bool singleChar = cycling || _typeAheadBuffer == runeLower;
        var needle = singleChar ? runeLower : _typeAheadBuffer;
        foreach (var node in TypeaheadCandidates(includeCursor: !singleChar))
        {
            if (!AsciiMatch.StartsWithLower(node.Text, needle))
                continue;
            if (ReferenceEquals(node, _cursor))
            {
                AnnounceCursor(null);
            }
            else
            {
                Reveal(node);
                MoveAndAnnounce(node);
            }
            return;
        }
    }

    // ── Input ──

    public override bool ReservesKey(KeyCombo combo)
    {
        if (!combo.Ctrl && !combo.Alt && !combo.Shift)
        {
            if (combo.Key == Key.Up || combo.Key == Key.Down
                || combo.Key == Key.Left || combo.Key == Key.Right
                || combo.Key == Key.Home || combo.Key == Key.End) return true;
            if (combo.Key == Key.Enter && _activateItems) return true;
            if (combo.Key.IsChar(out _) || combo.Key == Key.Space) return true; // type-ahead
        }
        return base.ReservesKey(combo);
    }

    protected override bool OnInput(in InputEvent input)
    {
        if (_cursor is not { } cursor)
        {
            switch (input.Kind)
            {
                case InputKind.MoveDown or InputKind.MoveUp
                    or InputKind.MoveRight or InputKind.MoveLeft
                    or InputKind.MoveToDocStart or InputKind.MoveToLineStart
                    or InputKind.MoveToDocEnd or InputKind.MoveToLineEnd:
                    AnnounceEmpty();
                    return true;
                default:
                    return false;
            }
        }
        var siblings = SiblingsOf(cursor);
        int at = siblings.IndexOf(cursor);
        switch (input.Kind)
        {
            case InputKind.MoveDown:
                if (at + 1 < siblings.Count)
                    MoveAndAnnounce(siblings[at + 1]);
                else
                    MoveAndAnnounce(siblings[0], siblings.Count > 1 ? Boundary.Bottom : null);
                return true;
            case InputKind.MoveUp:
                if (at > 0)
                    MoveAndAnnounce(siblings[at - 1]);
                else
                    MoveAndAnnounce(siblings[^1], siblings.Count > 1 ? Boundary.Top : null);
                return true;
            case InputKind.MoveRight:
                if (cursor.IsBranch && !cursor.Expanded)
                {
                    cursor.Expanded = true;
                    AnnounceCursor(null);
                    Post(() => OnNodeToggled(cursor, true));
                }
                else if (cursor.IsBranch)
                {
                    MoveAndAnnounce(cursor.Children[0]);
                }
                else
                {
                    AnnounceCursor(Boundary.Right);
                }
                return true;
            case InputKind.MoveLeft:
                if (cursor is { IsBranch: true, Expanded: true })
                {
                    cursor.Expanded = false;
                    AnnounceCursor(null);
                    Post(() => OnNodeToggled(cursor, false));
                }
                else if (cursor.Parent is { } parent)
                {
                    MoveAndAnnounce(parent);
                }
                else
                {
                    AnnounceCursor(Boundary.Left);
                }
                return true;
            case InputKind.MoveToDocStart or InputKind.MoveToLineStart:
                if (at != 0)
                    MoveAndAnnounce(siblings[0]);
                return true;
            case InputKind.MoveToDocEnd or InputKind.MoveToLineEnd:
                if (at != siblings.Count - 1)
                    MoveAndAnnounce(siblings[^1]);
                return true;
            case InputKind.Activate when _activateItems:
                PostActivated();
                return true;
            case InputKind.TypeChar:
                if (System.Text.Rune.IsValid((int)input.Ch))
                    HandleTypeAhead(char.ConvertFromUtf32((int)input.Ch));
                return true;
            default:
                return false;
        }
    }
}

/// <summary>The plain-text tree — <see cref="TreeView{T}"/> over
/// <see cref="TreeNode"/> values.</summary>
public class TreeView : TreeView<TreeNode>
{
    public TreeView(
        IWidgetContainer parent, string name, IReadOnlyList<TreeNode> roots,
        bool numbered = false, bool activateItems = false)
        : base(parent, name, roots, numbered, activateItems)
    {
    }
}
