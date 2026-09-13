using Srui.Core;

namespace Srui;

/// <summary>A node a <see cref="TreeView{T}"/> holds: a line, children,
/// an expansion flag, a checked state. State-bearing application node
/// types derive from the self-typed form (<c>class StatNode :
/// TreeNode&lt;StatNode&gt;</c>) so traversal hands them back without
/// casts, and add fields of their own; plain text rides the sealed
/// <see cref="TreeNode"/>. The widget stamps <see cref="Parent"/> links
/// when it takes the roots (and on <see cref="TreeView{T}.Refresh"/>
/// after structural mutation); children lists are the application's
/// to build.</summary>
public partial class TreeNode<T> : Element where T : TreeNode<T>
{
    public TreeNode(string text) => Value = text;

    /// <summary>The node's line: spoken by readers, matched by
    /// typeahead.</summary>
    [Field] public partial string? Value { get; set; }

    /// <summary>Whether the branch is open. Meaningless on a leaf.</summary>
    [Field] public partial bool Expanded { get; set; }

    /// <summary>Checked state in a multi-select tree; null until
    /// touched, which readers treat as unchecked.</summary>
    [Field] public partial bool? Checked { get; set; }

    /// <summary>How many children the node has; readers speak a
    /// branch's expansion and count, and nothing for a leaf.</summary>
    [Field] public int ChildCount => Children.Count;

    /// <summary>Depth in the tree, roots at zero.</summary>
    [Field]
    public int Level
    {
        get
        {
            var level = 0;
            for (var p = Parent; p is not null; p = p.Parent)
                level++;
            return level;
        }
    }

    /// <summary>The node's children, in display order. Mutating the
    /// structure under a live widget needs a <see
    /// cref="TreeView{T}.Refresh"/> so parent links and the cursor
    /// keep up.</summary>
    public List<T> Children { get; } = [];

    /// <summary>Whether Space may check this node in a multi-select
    /// tree. Null is the default rule — leaves yes, branches no. A
    /// branch that opts in is an item with properties inside (a
    /// configurable thing), not a group; a leaf that opts out is
    /// display-only or activation-only.</summary>
    public bool? Checkable { get; set; }

    /// <summary>The owning node, stamped by the widget — null at root
    /// level.</summary>
    public T? Parent { get; internal set; }

    /// <summary>Whether this node has children.</summary>
    public bool IsBranch => Children.Count > 0;

    internal bool IsCheckable => Checkable ?? !IsBranch;
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
/// node's siblings and wrap within them silently, unlike a list's
/// marked wrap: the ring is short and a numbered tree's position
/// report betrays the seam by itself — making a branch a room rather
/// than a region of one long hallway.
/// Right on a branch opens it if closed and steps into its first
/// child, one gesture; Left collapses an open branch, then jumps to
/// the parent — the "where am I" recovery move. Home/End jump within
/// the siblings.
///
/// Typeahead searches outward from the cursor — nearer nodes in the
/// flattened visible order match first, so a name that repeats across
/// branches resolves to the topologically closest bearer — and then
/// falls back to nodes inside collapsed branches, outward again;
/// landing on one reveals it (its ancestors expand). A reveal stays
/// provisional until the user engages: the next typeahead landing
/// closes whatever the previous one opened that the new match does
/// not need — a match typed past was not the goal, so neither was
/// opening its parents — while arrows, Enter, or a check accept the
/// expansion as deliberate. Single letters cycle like a list's;
/// multi-letter prefixes search from the cursor out.
///
/// Enter is not claimed by default (the dialog convention — it falls
/// through to the layer's primary); <c>activateItems: true</c> claims
/// it and raises <see cref="Widget.Activated"/> for the selected
/// node.
///
/// A multi-select tree (<c>multiSelect: true</c>) lets the user check
/// leaves independently of the cursor: Space toggles the selected leaf
/// (Space leaves the typeahead buffer, the toggleWithSpace trade), and
/// non-checkable nodes refuse the toggle with a word. Checkability is
/// per node (TreeNode.Checkable): leaves by default, branches on
/// opt-in — a checkable branch is an item with properties inside, not
/// a group. Enter stays whatever the activateItems choice made it.</summary>
public partial class TreeView<T> : Widget where T : TreeNode<T>
{
    /// <summary>Timeout for resetting the typeahead buffer (milliseconds
    /// of host time).</summary>
    private const ulong TypeAheadTimeoutMs = 400;

    private List<T> _roots;
    private Func<IReadOnlyList<T>>? _source;
    private T? _cursor;
    private readonly bool _numbered;
    private readonly bool _activateItems;
    private readonly bool _multiSelect;
    private string _typeAheadBuffer = "";
    private ulong? _lastKeystrokeMs;
    /// <summary>Branches a typeahead landing opened (the
    /// collapsed-content fallback), still provisional: the next
    /// typeahead landing closes the ones the new match does not sit
    /// under — a match the user typed past was not the goal, so
    /// neither was opening its parents — while any other engagement
    /// (arrows, Enter, a check, a programmatic move) accepts them as
    /// the tree's real state. Only branches the reveal itself flipped
    /// are recorded, so a user-opened ancestor never closes.</summary>
    private readonly List<T> _typeaheadOpened = [];

    public TreeView(
        IWidgetContainer parent, string name, IReadOnlyList<T> roots,
        bool numbered = false, bool activateItems = false, bool multiSelect = false)
        : base(parent, name, Role.Tree)
    {
        _roots = new List<T>(roots);
        StampParents(_roots, null);
        _cursor = _roots.Count > 0 ? _roots[0] : null;
        _numbered = numbered;
        _activateItems = activateItems;
        _multiSelect = multiSelect;
    }

    // ── Fields ──

    /// <summary>Whether leaves are checked independently of the cursor.</summary>
    [Field] public bool MultiSelect => _multiSelect;

    /// <summary>How many roots the tree has.</summary>
    [Field] public int Count => Roots.Count;

    /// <summary>The cursor's position among its siblings, when the tree
    /// counts.</summary>
    [Field]
    public Position? Position
    {
        get
        {
            if (!_numbered || Cursor is not { } c)
                return null;
            var siblings = SiblingsOf(c);
            return new Position(siblings.IndexOf(c), siblings.Count);
        }
    }

    protected internal override Element? CurrentItem => Cursor;

    // ── Roots ──

    /// <summary>The root nodes. Stored on the tree unless
    /// <see cref="BindRoots"/> gave them a source; setting replaces the
    /// tree (parent links stamped, cursor to the first root).</summary>
    public IReadOnlyList<T> Roots
    {
        get
        {
            if (_source is not { } source)
                return _roots;
            var roots = source();
            StampParents(roots, null);
            return roots;
        }
        set => SetRoots(value);
    }

    /// <summary>Replace the tree; equivalent to setting <see cref="Roots"/>.</summary>
    public void SetRoots(IReadOnlyList<T> roots)
    {
        if (_source is not null)
            throw new InvalidOperationException("the roots are bound; change them at the source");
        _roots = new List<T>(roots);
        StampParents(_roots, null);
        _cursor = _roots.Count > 0 ? _roots[0] : null;
        _typeaheadOpened.Clear();
        Engine.Touch();
    }

    /// <summary>Make the program's collection the tree's roots: every
    /// read asks the source and stamps parent links, so structure
    /// follows the model with no call to the tree.</summary>
    public void BindRoots(Func<IReadOnlyList<T>> source)
    {
        _source = source;
        Engine.Touch();
    }

    /// <summary>Re-stamp parent links and re-seat the cursor after the
    /// application mutated node children in place. The cursor keeps
    /// its node when the node is still in the tree, else it falls to
    /// the first root, which the tick end reads.</summary>
    public void Refresh()
    {
        StampParents(_roots, null);
        _typeaheadOpened.Clear();
        Engine.Touch();
    }

    // ── The cursor ──

    /// <summary>The cursor, re-seated on the first root when its node
    /// left the tree.</summary>
    private T? Cursor
    {
        get
        {
            var roots = Roots;
            if (_cursor is { } c && InTree(roots, c))
                return c;
            _cursor = roots.Count > 0 ? roots[0] : null;
            return _cursor;
        }
    }

    /// <summary>The selected node, or null when the tree is empty.</summary>
    public T? SelectedNode => Cursor;

    /// <summary>Move the selection to a node in the tree, revealing it
    /// (ancestors expand).</summary>
    public void SelectNode(T node)
    {
        if (!InTree(Roots, node))
            throw new ArgumentException("the node is not in this tree", nameof(node));
        _typeaheadOpened.Clear();
        Reveal(node);
        _cursor = node;
        Engine.Touch();
    }

    /// <summary>The user expanded or collapsed a branch; the arguments
    /// are the node and its new state.</summary>
    public event Action<T, bool>? NodeToggled;

    protected virtual void OnNodeToggled(T node, bool expanded) =>
        NodeToggled?.Invoke(node, expanded);

    // ── Multi-select checked state ──

    /// <summary>Whether the node is checked.</summary>
    public bool IsChecked(T node) => node.Checked == true;

    /// <summary>Check or uncheck a node programmatically. Throws on a
    /// non-checkable node.</summary>
    public void SetChecked(T node, bool value)
    {
        if (!node.IsCheckable)
            throw new InvalidOperationException("the node is not checkable");
        node.Checked = value;
        Engine.Touch();
    }

    /// <summary>The checked nodes, in tree order (expansion ignored —
    /// a check inside a since-collapsed branch still counts).</summary>
    public IReadOnlyList<T> CheckedNodes
    {
        get
        {
            var result = new List<T>();
            foreach (var node in AllNodes())
                if (node.Checked == true)
                    result.Add(node);
            return result;
        }
    }

    /// <summary>The user checked or unchecked a leaf; the arguments
    /// are the node and its new state.</summary>
    public event Action<T, bool>? NodeChecked;

    protected virtual void OnNodeChecked(T node, bool isChecked) =>
        NodeChecked?.Invoke(node, isChecked);

    /// <summary>The user toggled the selected leaf: flip the node's
    /// field (the tick end reads it), notify the program. Branches
    /// refuse with a word — silence would feel like a dead key.</summary>
    private void ToggleSelected()
    {
        _typeaheadOpened.Clear();
        if (Cursor is not { } cursor)
            return;
        if (!cursor.IsCheckable)
        {
            Announce("Checks apply to items, not groups.");
            return;
        }
        var isChecked = !(cursor.Checked ?? false);
        cursor.Checked = isChecked;
        Engine.Touch();
        Post(() => OnNodeChecked(cursor, isChecked));
    }

    // ── Structure helpers ──

    private static void StampParents(IReadOnlyList<T> nodes, T? parent)
    {
        foreach (var node in nodes)
        {
            node.Parent = parent;
            StampParents(node.Children, node);
        }
    }

    private List<T> SiblingsOf(T node) => node.Parent?.Children ?? RootsAsList();

    private List<T> RootsAsList() => Roots as List<T> ?? new List<T>(Roots);

    private static bool InTree(IReadOnlyList<T> roots, T node)
    {
        var top = node;
        while (top.Parent is { } p)
            top = p;
        return roots.Contains(top) && Lineage(node);

        static bool Lineage(T n) => n.Parent is not { } p || (p.Children.Contains(n) && Lineage(p));
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
        void Walk(IReadOnlyList<T> nodes)
        {
            foreach (var node in nodes)
            {
                result.Add(node);
                if (node.Expanded)
                    Walk(node.Children);
            }
        }
        Walk(Roots);
        return result;
    }

    /// <summary>Every node, in tree order, expansion ignored.</summary>
    private List<T> AllNodes()
    {
        var result = new List<T>();
        void Walk(IReadOnlyList<T> nodes)
        {
            foreach (var node in nodes)
            {
                result.Add(node);
                Walk(node.Children);
            }
        }
        Walk(Roots);
        return result;
    }

    private void MoveAndNotify(T node)
    {
        _cursor = node;
        Engine.Touch();
        PostChanged();
    }

    // ── Typeahead ──

    private static string TextOf(T node) => node.Value ?? "";

    /// <summary>Candidates in match priority order: the cursor's
    /// sibling ring first (outward both ways, wrapping — the room
    /// you're standing in), then the rest of the visible tree by tree
    /// distance (edges up and down between the nodes, so a cousin at
    /// your level outranks a leaf buried in the neighbor's subtree —
    /// proximity is structural, never the flattened depth-first
    /// index), then hidden nodes the same way — collapsed content
    /// matches only when nothing reachable does. <paramref
    /// name="includeCursor"/> puts the cursor itself first (the
    /// multi-letter prefix convention; single-letter cycling starts
    /// past it).</summary>
    private List<T> TypeaheadCandidates(bool includeCursor)
    {
        var result = new List<T>();
        if (Cursor is not { } cursor)
            return result;
        var seen = new HashSet<T>(ReferenceEqualityComparer.Instance) { cursor };
        if (includeCursor)
            result.Add(cursor);

        // The room: siblings outward from the cursor, wrapping, the
        // same ring navigation walks.
        var siblings = SiblingsOf(cursor);
        int at = siblings.IndexOf(cursor);
        for (int d = 1; d < siblings.Count; d++)
        {
            var after = siblings[(at + d) % siblings.Count];
            if (seen.Add(after)) result.Add(after);
            var before = siblings[(at - d + siblings.Count) % siblings.Count];
            if (seen.Add(before)) result.Add(before);
        }

        var visible = VisibleNodes();
        result.AddRange(RankByDistance(cursor, visible,
            visible.Where(n => !seen.Contains(n))));
        foreach (var node in visible)
            seen.Add(node);

        var all = AllNodes();
        result.AddRange(RankByDistance(cursor, all,
            all.Where(n => !seen.Contains(n))));
        return result;
    }

    /// <summary>Cycling candidates: the visible tree in flat order
    /// rotated to start past the cursor, hidden nodes after — the
    /// rotation every repeat-press walks so no bearer is starved.</summary>
    private List<T> RotationCandidates()
    {
        var result = new List<T>();
        if (Cursor is not { } cursor)
            return result;
        var visible = VisibleNodes();
        int ci = visible.IndexOf(cursor);
        for (int d = 1; d <= visible.Count; d++)
            result.Add(visible[(ci + d) % visible.Count]);
        var visibleSet = new HashSet<T>(visible, ReferenceEqualityComparer.Instance);
        var all = AllNodes();
        result.AddRange(RankByDistance(cursor, all,
            all.Where(n => !visibleSet.Contains(n))));
        return result;
    }

    /// <summary>Order candidates by tree distance from the cursor,
    /// ties broken by flat-order nearness, after before behind.</summary>
    private static List<T> RankByDistance(T cursor, List<T> flat, IEnumerable<T> candidates)
    {
        var indexOf = new Dictionary<T, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < flat.Count; i++)
            indexOf[flat[i]] = i;
        int ci = indexOf.TryGetValue(cursor, out var c) ? c : 0;
        return [.. candidates
            .OrderBy(n => TreeDistance(cursor, n))
            .ThenBy(n => Math.Abs(indexOf[n] - ci))
            .ThenBy(n => indexOf[n] > ci ? 0 : 1)];
    }

    /// <summary>Edges between two nodes — up from one, down to the
    /// other; different roots meet through a virtual root above them
    /// all.</summary>
    private static int TreeDistance(T a, T b)
    {
        var upFromA = new Dictionary<T, int>(ReferenceEqualityComparer.Instance);
        int climb = 0;
        T top = a;
        for (T? n = a; n is not null; n = n.Parent)
        {
            upFromA[n] = climb++;
            top = n;
        }
        int up = 0;
        for (T? n = b; n is not null; n = n.Parent, up++)
            if (upFromA.TryGetValue(n, out var down))
                return down + up;
        // Through the virtual root: a to its root, up and over, b's
        // root down to b (up counted nodes, so b's edges are up - 1).
        return upFromA[top] + (up - 1) + 2;
    }

    /// <summary>Reveal a typeahead landing and settle the previous
    /// one's debt: branches the last landing opened close again unless
    /// the new match needs them, and whatever this reveal flips open
    /// becomes the new provisional set.</summary>
    private void RevealForTypeahead(T node)
    {
        var keep = new HashSet<T>(ReferenceEqualityComparer.Instance);
        for (var p = node.Parent; p is not null; p = p.Parent)
            keep.Add(p);
        foreach (var stale in _typeaheadOpened)
            if (!keep.Contains(stale))
                stale.Expanded = false;
        _typeaheadOpened.RemoveAll(n => !keep.Contains(n));
        for (var p = node.Parent; p is not null; p = p.Parent)
            if (!p.Expanded)
            {
                p.Expanded = true;
                _typeaheadOpened.Add(p);
            }
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
        // A first press hunts by proximity — the nearest bearer wins.
        // Repeats rotate in flat order instead: proximity re-ranked
        // from each new cursor would bounce between two close matches
        // and starve the rest; a rotation visits every bearer.
        var candidates = cycling ? RotationCandidates() : TypeaheadCandidates(includeCursor: !singleChar);
        foreach (var node in candidates)
        {
            if (!AsciiMatch.StartsWithLower(TextOf(node), needle))
                continue;
            if (ReferenceEquals(node, _cursor))
            {
                RereadItem();
            }
            else
            {
                RevealForTypeahead(node);
                MoveAndNotify(node);
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
        // Anything but typeahead accepts the last landing's reveals:
        // engaging with the tree from here means this is where the
        // user meant to be. (The multi-select Space confirm rides the
        // TypeChar kind; ToggleSelected clears for it.)
        if (input.Kind is not InputKind.TypeChar)
            _typeaheadOpened.Clear();
        if (Cursor is not { } cursor)
        {
            switch (input.Kind)
            {
                case InputKind.MoveDown or InputKind.MoveUp
                    or InputKind.MoveRight or InputKind.MoveLeft
                    or InputKind.MoveToDocStart or InputKind.MoveToLineStart
                    or InputKind.MoveToDocEnd or InputKind.MoveToLineEnd:
                    Reread(Fields.Count);
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
                MoveAndNotify(siblings[(at + 1) % siblings.Count]);
                return true;
            case InputKind.MoveUp:
                MoveAndNotify(siblings[(at - 1 + siblings.Count) % siblings.Count]);
                return true;
            case InputKind.MoveRight:
                if (cursor.IsBranch)
                {
                    // Opening and entering are one gesture: you pressed
                    // right because you want in, so the first child
                    // speaks — landing inside IS the expansion report.
                    bool opened = !cursor.Expanded;
                    cursor.Expanded = true;
                    MoveAndNotify(cursor.Children[0]);
                    if (opened)
                        Post(() => OnNodeToggled(cursor, true));
                }
                else
                {
                    AnnounceBoundary(Boundary.Right);
                }
                return true;
            case InputKind.MoveLeft:
                if (cursor is { IsBranch: true, Expanded: true })
                {
                    cursor.Expanded = false;
                    Engine.Touch();
                    Post(() => OnNodeToggled(cursor, false));
                }
                else if (cursor.Parent is { } parent)
                {
                    MoveAndNotify(parent);
                }
                else
                {
                    AnnounceBoundary(Boundary.Left);
                }
                return true;
            case InputKind.MoveToDocStart or InputKind.MoveToLineStart:
                if (at != 0)
                    MoveAndNotify(siblings[0]);
                return true;
            case InputKind.MoveToDocEnd or InputKind.MoveToLineEnd:
                if (at != siblings.Count - 1)
                    MoveAndNotify(siblings[^1]);
                return true;
            case InputKind.Activate when _activateItems:
                PostActivated();
                return true;
            case InputKind.TypeChar when _multiSelect && input.IsChar(' '):
                ToggleSelected();
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
        bool numbered = false, bool activateItems = false, bool multiSelect = false)
        : base(parent, name, roots, numbered, activateItems, multiSelect)
    {
    }
}
