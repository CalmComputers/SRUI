namespace Srui.Core;

/// <summary>The core engine — ties the tree, widget behavior dispatch,
/// focus, and the output event queue together. The host drives it: push
/// logical input in with HandleInput, mutate the tree through the
/// insertion/removal methods, and drain coalesced output events when
/// convenient (typically after each input event).</summary>
internal sealed class CoreUi
{
    private readonly Tree _tree = new();
    private readonly Dictionary<NodeId, WidgetBehavior> _behaviors = new();
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

    /// <summary>Insert a behavior-less node (groups, labels) at the end of
    /// the parent's children (or the active layer's roots).</summary>
    public NodeId Insert(NodeId parent, WidgetLabel label) =>
        _tree.Insert(parent, int.MaxValue, label);

    /// <summary>Insert a behavior-less node at a specific child position.</summary>
    public NodeId InsertAt(NodeId parent, int index, WidgetLabel label) =>
        _tree.Insert(parent, index, label);

    /// <summary>Insert a node with widget behavior at the end of the
    /// parent's children.</summary>
    public NodeId InsertWidget(NodeId parent, WidgetLabel label, WidgetBehavior behavior)
    {
        var id = _tree.Insert(parent, int.MaxValue, label);
        _behaviors[id] = behavior;
        return id;
    }

    /// <summary>Insert a node with widget behavior at a specific child
    /// position.</summary>
    public NodeId InsertWidgetAt(NodeId parent, int index, WidgetLabel label, WidgetBehavior behavior)
    {
        var id = _tree.Insert(parent, index, label);
        _behaviors[id] = behavior;
        return id;
    }

    public NodeId Button(NodeId parent, string name) =>
        InsertWidget(parent, new WidgetLabel(name, Role.Button), new ButtonBehavior());

    public NodeId Checkbox(NodeId parent, string name, bool isChecked)
    {
        var label = new WidgetLabel(name, Role.CheckBox)
        {
            Value = CheckBoxBehavior.ValueText(isChecked),
        };
        return InsertWidget(parent, label, new CheckBoxBehavior(isChecked));
    }

    /// <summary>A single-selection listbox. Numbered adds "N of M" to
    /// announcements and the focus state text.</summary>
    public NodeId Listbox(NodeId parent, string name, List<string> items, bool numbered)
    {
        var behavior = new ListBoxBehavior(items, numbered);
        var label = new WidgetLabel(name, Role.ListBox);
        behavior.SyncLabel(label);
        return InsertWidget(parent, label, behavior);
    }

    /// <summary>Replace a listbox's items (selection clamped).
    /// Re-announces if the listbox is focused and its label changed.</summary>
    public void SetListItems(NodeId id, List<string> items) =>
        WithWidget<ListBoxBehavior>(id, (list, label) => list.SetItems(items, label));

    /// <summary>Move a listbox's selection programmatically (clamped). A
    /// change while focused speaks like a user-driven one — the item
    /// alone, not a full re-announcement.</summary>
    public void SetListSelected(NodeId id, int index)
    {
        if (_behaviors.GetValueOrDefault(id) is not ListBoxBehavior list)
            return;
        var node = _tree.Get(id);
        if (node is null)
            return;
        var prev = list.Selected;
        list.SetSelected(index, node.Label);
        if (list.Selected != prev && _tree.Focus == id
            && list.ChangeEvent(id, null) is AccessibilityEvent ev)
            _events.Add(new CoreEvent.Acc(ev));
    }

    /// <summary>Mutate a node's widget state and label together;
    /// re-announces if the node is focused and the label changed. All the
    /// typed setters route through this.</summary>
    private void WithWidget<T>(NodeId id, Action<T, WidgetLabel> mutate) where T : WidgetBehavior
    {
        if (_behaviors.GetValueOrDefault(id) is not T typed)
            return;
        var node = _tree.Get(id);
        if (node is null)
            return;
        var before = node.Label.Clone();
        mutate(typed, node.Label);
        var changed = !node.Label.ContentEquals(before);
        if (RecoverUnreachableFocus())
            return;
        if (_tree.Focus == id && changed)
            EmitFocused(id);
    }

    public NodeId Slider(NodeId parent, string name, int value, int min, int max) =>
        SliderWidget(parent, name, new SliderBehavior(value, min, max));

    /// <summary>Insert a pre-configured slider (steps, unit).</summary>
    public NodeId SliderWidget(NodeId parent, string name, SliderBehavior slider)
    {
        var label = new WidgetLabel(name, Role.Slider);
        slider.SyncLabel(label);
        return InsertWidget(parent, label, slider);
    }

    /// <summary>Move a slider programmatically (clamped). A change while
    /// focused speaks like a user-driven one — the new value alone — so
    /// ticking progress bars stay terse.</summary>
    public void SetSliderValue(NodeId id, int value)
    {
        if (_behaviors.GetValueOrDefault(id) is not SliderBehavior slider)
            return;
        var node = _tree.Get(id);
        if (node is null)
            return;
        var prev = slider.Value;
        slider.SetValue(value, node.Label);
        if (slider.Value != prev && _tree.Focus == id)
            _events.Add(new CoreEvent.Acc(slider.ChangeEvent(id)));
    }

    public NodeId TabControl(NodeId parent, string name, List<string> tabs, int active)
    {
        var behavior = new TabControlBehavior(tabs, active);
        var label = new WidgetLabel(name, Role.TabControl);
        behavior.SyncLabel(label);
        return InsertWidget(parent, label, behavior);
    }

    /// <summary>Switch a tab control programmatically (clamped). A change
    /// while focused speaks like a user-driven one — the tab name alone.</summary>
    public void SetActiveTab(NodeId id, int index)
    {
        if (_behaviors.GetValueOrDefault(id) is not TabControlBehavior tabs)
            return;
        var node = _tree.Get(id);
        if (node is null)
            return;
        var prev = tabs.Active;
        tabs.SetActive(index, node.Label);
        if (tabs.Active != prev && _tree.Focus == id
            && tabs.ChangeEvent(id) is AccessibilityEvent ev)
            _events.Add(new CoreEvent.Acc(ev));
    }

    public NodeId ShortcutField(NodeId parent, string name)
    {
        var behavior = new ShortcutFieldBehavior();
        var label = new WidgetLabel(name, Role.ShortcutField);
        behavior.SyncLabel(label);
        return InsertWidget(parent, label, behavior);
    }

    /// <summary>Set or clear a shortcut field's combo programmatically.</summary>
    public void SetShortcutCombo(NodeId id, KeyCombo? combo) =>
        WithWidget<ShortcutFieldBehavior>(id, (field, label) => field.SetCombo(combo, label));

    public NodeId FilterListbox(NodeId parent, string name, List<string> items)
    {
        var behavior = new FilterListBoxBehavior(items);
        var label = new WidgetLabel(name, Role.ListBox);
        behavior.SyncLabel(label);
        return InsertWidget(parent, label, behavior);
    }

    /// <summary>Replace a filter list's items (filter kept, selection
    /// reset). Re-announces if focused and the label changed.</summary>
    public void SetFilterItems(NodeId id, List<string> items) =>
        WithWidget<FilterListBoxBehavior>(id, (filter, label) => filter.SetItems(items, label));

    /// <summary>Clear a filter list's query and selection.</summary>
    public void ClearFilter(NodeId id) =>
        WithWidget<FilterListBoxBehavior>(id, (filter, label) => filter.ClearFilter(label));

    /// <summary>An edit box. A null name announces as "role value" only.</summary>
    public NodeId Editbox(NodeId parent, string? name, string text, bool multiline)
    {
        var behavior = new EditBoxBehavior(text, multiline);
        var role = Role.Edit(false, multiline);
        var label = name is not null ? new WidgetLabel(name, role) : WidgetLabel.Nameless(role);
        behavior.SyncLabel(label);
        return InsertWidget(parent, label, behavior);
    }

    /// <summary>Replace an editbox's content (cursor clamped, selection
    /// cleared). Re-announces if focused and the label changed.</summary>
    public void SetEditboxText(NodeId id, string text) =>
        WithWidget<EditBoxBehavior>(id, (edit, label) => edit.SetText(text, label));

    /// <summary>Toggle an editbox's read-only state.</summary>
    public void SetEditboxReadOnly(NodeId id, bool readOnly) =>
        WithWidget<EditBoxBehavior>(id, (edit, label) => edit.SetReadOnly(readOnly, label));

    public NodeId Group(NodeId parent, string name) =>
        Insert(parent, new WidgetLabel(name, Role.Group));

    /// <summary>A custom widget — focusable, no spoken role, no built-in
    /// behavior. Every key falls through the core to the host's bindings;
    /// the announcement is the name (plus value, states, description, and
    /// shortcut when set).</summary>
    public NodeId Custom(NodeId parent, string name) =>
        Insert(parent, new WidgetLabel(name, Role.Custom));

    public NodeId TextLabel(NodeId parent, string text) =>
        Insert(parent, new WidgetLabel(text, Role.Label));

    /// <summary>Remove a node and its subtree. If focus was inside the
    /// removed subtree, it recovers to the nearest surviving focusable
    /// node and the recovery is announced.</summary>
    public void Remove(NodeId id)
    {
        var parent = _tree.Parent(id);
        var focus = _tree.Focus;
        var focusInside = !focus.IsNone && (focus == id || IsAncestor(id, focus));

        RemoveBehaviorSubtree(id);
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

    private void RemoveBehaviorSubtree(NodeId id)
    {
        _behaviors.Remove(id);
        foreach (var child in _tree.Children(id))
            RemoveBehaviorSubtree(child);
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

    /// <summary>Typed access to a node's widget behavior.</summary>
    public T? Widget<T>(NodeId id) where T : WidgetBehavior =>
        _behaviors.GetValueOrDefault(id) as T;

    // ── Label mutation ──

    /// <summary>Mutate a node's label. If the node is focused and the
    /// label actually changed, the focus is re-announced (coalescing
    /// collapses bursts). If the mutation made the focused node
    /// unreachable (hidden, disabled, or inside a newly hidden subtree),
    /// focus recovers to the nearest focusable node and announces there
    /// instead.</summary>
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
            ? label.States | States.Hidden
            : label.States & ~States.Hidden);

    public void SetDisabled(NodeId id, bool disabled) =>
        UpdateLabel(id, label => label.States = disabled
            ? label.States | States.Disabled
            : label.States & ~States.Disabled);

    public void SetNodeName(NodeId id, string name) =>
        UpdateLabel(id, label => label.Name = name);

    public void SetNodeDescription(NodeId id, string description) =>
        UpdateLabel(id, label => label.Description = description);

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
    /// in itself (visible, enabled, focusable role) and not inside a
    /// hidden subtree. Gates focus recovery and primary/cancel activation.</summary>
    private bool Reachable(NodeId id)
    {
        var node = _tree.Get(id);
        if (node is null || !WidgetLabel.IsFocusable(node.Label.Role, node.Label.States))
            return false;
        for (var parent = _tree.Parent(id); !parent.IsNone; parent = _tree.Parent(parent))
        {
            var p = _tree.Get(parent);
            if (p is not null && (p.Label.States & States.Hidden) != 0)
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
        if (node is not null)
            _events.Add(new CoreEvent.Acc(new AccessibilityEvent.Focused(
                id, node.Label.Clone(), EmptyContext)));
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
        if (node is null)
            return;
        _events.Add(new CoreEvent.Acc(new AccessibilityEvent.Focused(
            id, node.Label.Clone(), ContextLabelsFor(id))));
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
            if (node is not null && node.Label.Role.Kind == RoleKind.Label
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
    /// and announced. Behaviors of the popped nodes are dropped.</summary>
    public void PopLayer()
    {
        foreach (var root in _tree.Roots)
            RemoveBehaviorSubtree(root);
        var restored = _tree.PopLayer();
        _focusMemory.Gc(_tree);
        if (!restored.IsNone)
            EmitFocused(restored);
    }

    // ── Input dispatch ──

    /// <summary>Dispatch one logical input. Claim order: the focused
    /// node's behavior first, then framework navigation, then widget
    /// shortcuts. True if consumed; the host routes unconsumed input to
    /// its own bindings.</summary>
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

        // 1. Focused widget gets first claim.
        var focused = _tree.Focus;
        if (!focused.IsNone && _behaviors.GetValueOrDefault(focused) is WidgetBehavior behavior)
        {
            var node = _tree.Get(focused);
            if (node is not null)
            {
                var ctx = new WidgetCtx(focused, node.Label, _events, _nowMs, _clipboard);
                if (behavior.HandleInput(input, ctx))
                    return true;
            }
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
            if (node is not null
                && WidgetLabel.IsFocusable(node.Label.Role, node.Label.States)
                && (node.Label.States & States.Hidden) == 0)
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

    /// <summary>Queue a free-form announcement ("Nothing to delete",
    /// status messages) for the readers.</summary>
    public void Announce(string text) =>
        _events.Add(new CoreEvent.Acc(new AccessibilityEvent.Announce(text)));

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
