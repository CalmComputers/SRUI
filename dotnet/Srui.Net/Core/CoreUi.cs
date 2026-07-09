namespace Srui.Core;

/// <summary>The core engine — ties the tree, focus, navigation, and the
/// output event queue together. Widget behavior lives in the public
/// Widget classes: dispatch hands the focused node's input to its owning
/// widget, and widgets write their label state and emissions back through
/// the methods here. The host drives it: push logical input in with
/// HandleInput, mutate the tree, and drain coalesced output events when
/// convenient (typically after each input event).</summary>
internal sealed class CoreUi
{
    private readonly Tree _tree = new();
    private readonly FocusMemory _focusMemory = new();
    private List<CoreEvent> _events = new();
    /// <summary>Host-supplied monotonic clock in milliseconds. Drives
    /// typeahead timeouts and tickers; without SetNow calls, neither fires.</summary>
    private ulong _nowMs;
    /// <summary>Injected platform clipboard; every editbox gets it for free.</summary>
    private IClipboard _clipboard = new NoClipboard();
    private readonly List<Ticker> _tickers = new();
    private ulong _nextTickerId;

    private sealed class Ticker
    {
        public ulong Id;
        public ulong IntervalMs;
        public ulong NextFireMs;
    }

    /// <summary>Install a platform clipboard (defaults to a no-op).</summary>
    public void SetClipboard(IClipboard clipboard) => _clipboard = clipboard;

    public IClipboard Clipboard => _clipboard;

    /// <summary>The host clock, as of the last SetNow.</summary>
    public ulong Now => _nowMs;

    /// <summary>Advance the host clock (monotonic milliseconds). Call
    /// every loop iteration, not just on input: typeahead timeouts and
    /// tickers are checked here, so ticker resolution is the call cadence.</summary>
    public void SetNow(ulong nowMs)
    {
        _nowMs = nowMs;
        foreach (var ticker in _tickers)
        {
            if (nowMs >= ticker.NextFireMs)
            {
                _events.Add(new CoreEvent.Tick(ticker.Id));
                // Drift-tolerant: the next interval starts now, so a late
                // check fires once rather than bursting to catch up.
                ticker.NextFireMs = nowMs + ticker.IntervalMs;
            }
        }
    }

    /// <summary>Register a periodic ticker: a Tick event fires each time
    /// the interval elapses, observed at SetNow resolution. Returns the
    /// id carried by the events.</summary>
    public ulong AddTicker(ulong intervalMs)
    {
        _nextTickerId++;
        var interval = Math.Max(intervalMs, 1);
        _tickers.Add(new Ticker
        {
            Id = _nextTickerId,
            IntervalMs = interval,
            NextFireMs = _nowMs + interval,
        });
        return _nextTickerId;
    }

    public void RemoveTicker(ulong id) => _tickers.RemoveAll(t => t.Id == id);

    // ── Tree construction ──

    /// <summary>Insert a node at the end of the parent's children (or the
    /// active layer's roots). The owner is the widget object that handles
    /// the node's input and receives its events.</summary>
    public NodeId Insert(NodeId parent, WidgetLabel label, Widget? owner = null) =>
        _tree.Insert(parent, int.MaxValue, label, owner);

    /// <summary>Insert a node at a specific child position.</summary>
    public NodeId InsertAt(NodeId parent, int index, WidgetLabel label, Widget? owner = null) =>
        _tree.Insert(parent, index, label, owner);

    /// <summary>Remove a node and its subtree. If focus was inside the
    /// removed subtree, it recovers to the nearest surviving focusable
    /// node and the recovery is announced.</summary>
    public void Remove(NodeId id)
    {
        var parent = _tree.Parent(id);
        var focus = _tree.Focus;
        var focusInside = !focus.IsNone && (focus == id || IsAncestor(id, focus));

        _tree.Remove(id);
        _focusMemory.Gc(_tree);

        if (focusInside)
        {
            var next = Nav.RecoverFocus(_tree, parent);
            if (!next.IsNone)
            {
                _tree.SetFocus(next);
                EmitFocused(next);
            }
        }
    }

    private bool IsAncestor(NodeId ancestor, NodeId node)
    {
        for (var parent = _tree.Parent(node); !parent.IsNone; parent = _tree.Parent(parent))
            if (parent == ancestor)
                return true;
        return false;
    }

    // ── Accessors ──

    public Tree Tree => _tree;

    public NodeId Focus => _tree.Focus;

    public WidgetLabel? Label(NodeId id) => _tree.Get(id)?.Label;

    public Widget? OwnerOf(NodeId id) => _tree.Get(id)?.Owner;

    // ── Emission (widgets write their output here) ──

    public void Emit(CoreEvent ev) => _events.Add(ev);

    public void EmitAccessibility(AccessibilityEvent ev) => _events.Add(new CoreEvent.Acc(ev));

    /// <summary>Queue a free-form announcement ("Nothing to delete",
    /// status messages) for the readers.</summary>
    public void Announce(string text) =>
        EmitAccessibility(new AccessibilityEvent.Announce(text));

    // ── Label mutation ──

    /// <summary>Mutate a node's label. If the node is focused and the
    /// label actually changed, the focus is re-announced (coalescing
    /// collapses bursts). If the mutation made the focused node
    /// unreachable (hidden, disabled, or inside a newly hidden subtree),
    /// focus recovers to the nearest focusable node and announces there
    /// instead. Widgets syncing state during their own input handling
    /// write the label directly instead — their emission is the
    /// announcement.</summary>
    public void UpdateLabel(NodeId id, Action<WidgetLabel> mutate)
    {
        var node = _tree.Get(id);
        if (node is null)
            return;
        var before = node.Label.Clone();
        mutate(node.Label);
        var changed = !node.Label.ContentEquals(before);
        if (RecoverUnreachableFocus())
            return;
        if (_tree.Focus == id && changed)
            EmitFocused(id);
    }

    /// <summary>Show or hide a node (and, for navigation purposes, its
    /// subtree).</summary>
    public void SetHidden(NodeId id, bool hidden) =>
        UpdateLabel(id, label => label.States = hidden
            ? label.States | WidgetStates.Hidden
            : label.States & ~WidgetStates.Hidden);

    public void SetState(NodeId id, WidgetStates state, bool on) =>
        UpdateLabel(id, label => label.States = on
            ? label.States | state
            : label.States & ~state);

    /// <summary>Attach a shortcut to a widget: pressing the combo jumps to
    /// it, activates it, or both. A widget may carry any number of
    /// shortcuts; the first added is the one focus announcements speak.
    /// When several widgets bind the same combo, the first reachable one
    /// in depth-first tree order wins.</summary>
    public void AddShortcut(NodeId id, KeyCombo combo, ShortcutAction action) =>
        UpdateLabel(id, label => label.Shortcuts.Add(new WidgetShortcut(combo, action)));

    /// <summary>Remove every shortcut from a widget.</summary>
    public void ClearShortcuts(NodeId id) =>
        UpdateLabel(id, label => label.Shortcuts.Clear());

    /// <summary>When the focused node is no longer reachable (not
    /// focusable, or under a hidden ancestor), move focus to the nearest
    /// focusable node and announce it. True if focus moved.</summary>
    private bool RecoverUnreachableFocus()
    {
        var focused = _tree.Focus;
        if (focused.IsNone || Reachable(focused))
            return false;
        var parent = _tree.Parent(focused);
        var next = Nav.RecoverFocus(_tree, parent);
        if (!next.IsNone && next != focused)
        {
            _tree.SetFocus(next);
            EmitFocused(next);
            return true;
        }
        return false;
    }

    /// <summary>Whether the user can currently reach this node: focusable
    /// in itself (visible, enabled, focusable kind) and not inside a
    /// hidden subtree. Gates focus recovery and primary/cancel activation.</summary>
    private bool Reachable(NodeId id)
    {
        var node = _tree.Get(id);
        if (node is null || !node.Label.IsFocusableNow)
            return false;
        for (var parent = _tree.Parent(id); !parent.IsNone; parent = _tree.Parent(parent))
        {
            var p = _tree.Get(parent);
            if (p is not null && (p.Label.States & WidgetStates.Hidden) != 0)
                return false;
        }
        return true;
    }

    // ── Focus ──

    /// <summary>Move focus programmatically. Announces the newly focused
    /// node.</summary>
    public void SetFocus(NodeId id)
    {
        if (_tree.Contains(id))
            SetFocusInternal(id);
    }

    /// <summary>If nothing is focused, focus the first focusable node and
    /// announce it. Hosts call this once after building the initial UI.</summary>
    public bool EnsureFocus()
    {
        if (!_tree.Focus.IsNone)
            return false;
        var first = Nav.TabNext(_tree, NodeId.None);
        if (first.IsNone)
            return false;
        _tree.SetFocus(first);
        EmitFocused(first);
        return true;
    }

    private void SetFocusInternal(NodeId next)
    {
        var old = _tree.Focus;
        if (!old.IsNone)
        {
            if (old == next)
                return;
            // Remember the child we're leaving for container re-entry.
            var parent = _tree.Parent(old);
            if (!parent.IsNone)
                _focusMemory.Remember(parent, old);
        }
        _tree.SetFocus(next);
        EmitFocused(next);
    }

    private void EmitFocused(NodeId id)
    {
        var node = _tree.Get(id);
        if (node?.Owner is Widget owner)
            _events.Add(new CoreEvent.Acc(new AccessibilityEvent.Focused(
                owner, node.Label.ToInfo(), EmptyContext)));
    }

    private static readonly List<string> EmptyContext = new();

    /// <summary>Re-announce the focused node with its context labels (the
    /// names of Label-role siblings preceding it in child order). Hosts
    /// call this after a view transition, when the plain announcement
    /// would lack orientation.</summary>
    public void ReannounceWithContext()
    {
        var id = _tree.Focus;
        if (id.IsNone)
            return;
        var node = _tree.Get(id);
        if (node?.Owner is not Widget owner)
            return;
        _events.Add(new CoreEvent.Acc(new AccessibilityEvent.Focused(
            owner, node.Label.ToInfo(), ContextLabelsFor(id))));
    }

    private List<string> ContextLabelsFor(NodeId id)
    {
        var parent = _tree.Parent(id);
        IReadOnlyList<NodeId> siblings = parent.IsNone ? _tree.Roots : _tree.Children(parent);
        var result = new List<string>();
        foreach (var sibling in siblings)
        {
            if (sibling == id)
                break;
            var node = _tree.Get(sibling);
            if (node is not null && node.Label.IsContextLabel
                && !string.IsNullOrEmpty(node.Label.Name))
                result.Add(node.Label.Name);
        }
        return result;
    }

    // ── Layers ──

    /// <summary>Set the active layer's primary widget (Enter activates it
    /// when the focused widget doesn't claim Enter).</summary>
    public void SetPrimary(NodeId id) => _tree.SetPrimary(id);

    /// <summary>Set the active layer's cancel widget (Escape activates it).</summary>
    public void SetCancel(NodeId id) => _tree.SetCancel(id);

    /// <summary>Push a modal layer. New root nodes go into it; only it is
    /// navigable.</summary>
    public void PushLayer() => _tree.PushLayer();

    /// <summary>Pop the top layer. The previous layer's focus is restored
    /// and announced.</summary>
    public void PopLayer()
    {
        var restored = _tree.PopLayer();
        _focusMemory.Gc(_tree);
        if (!restored.IsNone)
            EmitFocused(restored);
    }

    // ── Input dispatch ──

    /// <summary>Dispatch one logical input. Claim order: the focused
    /// node's widget first, then framework navigation and layer defaults,
    /// then widget shortcuts. True if consumed; the host routes unconsumed
    /// input to its own bindings.</summary>
    public bool HandleInput(in InputEvent input)
    {
        // Establish focus if the tree has focusable content but no focus.
        if (_tree.Focus.IsNone)
        {
            var established = EnsureFocus();
            // A tab press that established focus is satisfied by it.
            if (established && input.Kind is InputKind.NavigateNext or InputKind.NavigatePrev)
                return true;
        }

        // 1. The focused widget gets first claim.
        var focused = _tree.Focus;
        if (!focused.IsNone && _tree.Get(focused)?.Owner is Widget owner)
        {
            if (owner.HandleEngineInput(input))
                return true;
        }

        // 2. Framework navigation and layer defaults.
        switch (input.Kind)
        {
            case InputKind.NavigateNext:
            {
                var next = Nav.TabNext(_tree, _tree.Focus);
                if (!next.IsNone)
                    SetFocusInternal(next);
                return true;
            }
            case InputKind.NavigatePrev:
            {
                var prev = Nav.TabPrev(_tree, _tree.Focus);
                if (!prev.IsNone)
                    SetFocusInternal(prev);
                return true;
            }
            case InputKind.TreeUp:
                TreeNavigate(TreeDirection.Up);
                return true;
            case InputKind.TreeDown:
                TreeNavDown();
                return true;
            case InputKind.TreeLeft:
                TreeNavigate(TreeDirection.Left);
                return true;
            case InputKind.TreeRight:
                TreeNavigate(TreeDirection.Right);
                return true;
            case InputKind.SpeakFocus:
                if (!_tree.Focus.IsNone)
                    EmitFocused(_tree.Focus);
                return true;
            case InputKind.Activate:
                // A hidden or disabled primary does not activate; the
                // input falls through unconsumed.
                if (!_tree.Primary.IsNone && Reachable(_tree.Primary))
                {
                    _events.Add(new CoreEvent.Activated(_tree.Primary));
                    return true;
                }
                break;
            case InputKind.Dismiss:
                // Same for the cancel widget: unconsumed Dismiss lets the
                // host fall back (e.g. closing a dialog directly).
                if (!_tree.Cancel.IsNone && Reachable(_tree.Cancel))
                {
                    _events.Add(new CoreEvent.Activated(_tree.Cancel));
                    return true;
                }
                break;
        }

        // 3. Widget shortcuts. Mnemonics arrive as Shortcut(ch) and match
        //    through their alt+letter combo form; everything else matches
        //    its own combo. An unreachable widget's shortcuts are inert,
        //    and unclaimed combos fall through to the host.
        if (KeyCombo.FromInput(input) is KeyCombo combo
            && Nav.FindShortcut(_tree, combo) is { } shortcut)
        {
            if (shortcut.Action is ShortcutAction.Jump or ShortcutAction.JumpAndActivate)
                SetFocusInternal(shortcut.Node);
            if (shortcut.Action is ShortcutAction.Activate or ShortcutAction.JumpAndActivate)
                _events.Add(new CoreEvent.Activated(shortcut.Node));
            return true;
        }
        return false;
    }

    private void TreeNavigate(TreeDirection direction)
    {
        var current = _tree.Focus;
        if (current.IsNone)
            return;
        var target = Nav.TreeNav(_tree, current, direction);
        if (!target.IsNone)
            SetFocusInternal(target);
    }

    /// <summary>Hierarchy-down with focus memory: re-entering a container
    /// returns to its last-focused child when that child still exists and
    /// is focusable; otherwise the first visible child.</summary>
    private void TreeNavDown()
    {
        var container = _tree.Focus;
        if (container.IsNone)
            return;
        var remembered = _focusMemory.Recall(container);
        if (!remembered.IsNone && _tree.Parent(remembered) == container)
        {
            var node = _tree.Get(remembered);
            if (node is not null && node.Label.IsFocusableNow)
            {
                SetFocusInternal(remembered);
                return;
            }
        }
        var target = Nav.TreeNav(_tree, container, TreeDirection.Down);
        if (!target.IsNone)
            SetFocusInternal(target);
    }

    // ── Output ──

    private static readonly List<CoreEvent> EmptyBatch = new();

    /// <summary>Drain the output queue, applying coalescing rules (see
    /// <see cref="Coalesce"/>). Empty drains return a shared list so the
    /// idle loop allocates nothing; treat the result as read-only.</summary>
    public List<CoreEvent> DrainEvents()
    {
        if (_events.Count == 0)
            return EmptyBatch;
        var batch = _events;
        _events = new List<CoreEvent>();
        return Coalesce.Apply(batch);
    }
}
