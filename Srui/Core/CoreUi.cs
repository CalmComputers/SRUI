namespace Srui.Core;

/// <summary>The core engine — ties the tree, focus, navigation, and the
/// output event queue together. Widget behavior lives in the public
/// Widget classes: dispatch hands the focused node's input to its owning
/// widget, and widgets write their structural traits and action events
/// back through the methods here. The host drives it: push logical
/// input in with HandleInput, mutate widgets, drain the queue, and end
/// the tick — which is where the engine describes the focused widget,
/// diffs it against what the user last heard, and appends the state
/// events readers render (architecture.md section 7).</summary>
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

    /// <summary>Where the live speech verbosity comes from. The engine
    /// consults it for the layer-restore mode, which decides what a
    /// restore describes; a bare engine with no source behaves as full.</summary>
    public Func<SpeechVerbosity>? VerbositySource;

    private static readonly SpeechVerbosity FullVerbosity = new();

    private SpeechVerbosity Verbosity => VerbositySource?.Invoke() ?? FullVerbosity;

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
                Emit(new CoreEvent.Tick(ticker.Id));
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

    // ── Dirtiness ──

    private bool _dirty;

    /// <summary>Whether anything since the last tick end could have
    /// changed what the user perceives: input, an emitted event, a
    /// focus move, a structural mutation, or a field write on a widget.
    /// A clean engine skips the tick-end description, so an idle loop
    /// allocates nothing.</summary>
    public bool Dirty => _dirty;

    /// <summary>Note that something may have changed.</summary>
    public void Touch() => _dirty = true;

    // ── Tree construction ──

    /// <summary>Insert a node at the end of the parent's children (or the
    /// active layer's roots). The owner is the widget object that handles
    /// the node's input and receives its events.</summary>
    public NodeId Insert(NodeId parent, WidgetLabel label, Widget? owner = null)
    {
        _dirty = true;
        return _tree.Insert(parent, int.MaxValue, label, owner);
    }

    /// <summary>Insert a node at a specific child position.</summary>
    public NodeId InsertAt(NodeId parent, int index, WidgetLabel label, Widget? owner = null)
    {
        _dirty = true;
        return _tree.Insert(parent, index, label, owner);
    }

    /// <summary>Remove a node and its subtree. If focus was inside the
    /// removed subtree, it recovers to the nearest surviving focusable
    /// node; the tick end reads the landing as a recovery.</summary>
    public void Remove(NodeId id)
    {
        var parent = _tree.Parent(id);
        var focus = _tree.Focus;
        var focusInside = !focus.IsNone && (focus == id || IsAncestor(id, focus));

        _tree.Remove(id);
        _focusMemory.Gc(_tree);
        _dirty = true;

        if (focusInside)
        {
            var next = Nav.RecoverFocus(_tree, parent);
            if (!next.IsNone)
                LandFocus(next, FocusCause.Recovery);
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

    public void Emit(CoreEvent ev)
    {
        _dirty = true;
        _events.Add(ev);
    }

    public void EmitAccessibility(AccessibilityEvent ev) => Emit(new CoreEvent.Acc(ev));

    /// <summary>Queue a free-form app-level announcement ("Nothing to
    /// delete", status messages) for the readers.</summary>
    public void Announce(string text) =>
        EmitAccessibility(new AccessibilityEvent.Announce(text));

    // ── Structural traits ──

    /// <summary>Show or hide a node (and, for navigation purposes, its
    /// subtree). Hiding the focused widget, or an ancestor, moves focus
    /// to the nearest reachable node; the tick end reads the landing as
    /// a recovery.</summary>
    public void SetHidden(NodeId id, bool hidden)
    {
        if (_tree.Get(id) is not { } node || node.Label.Hidden == hidden)
            return;
        node.Label.Hidden = hidden;
        _dirty = true;
        RecoverUnreachableFocus();
    }

    public void SetDisabled(NodeId id, bool disabled)
    {
        if (_tree.Get(id) is not { } node || node.Label.Disabled == disabled)
            return;
        node.Label.Disabled = disabled;
        _dirty = true;
    }

    /// <summary>Attach a shortcut to a widget: pressing the combo jumps to
    /// it, activates it, or both. A widget may carry any number of
    /// shortcuts; the first added is the one focus announcements speak.
    /// When several widgets bind the same combo, the first reachable one
    /// in depth-first tree order wins.</summary>
    public void AddShortcut(NodeId id, KeyCombo combo, ShortcutAction action)
    {
        if (_tree.Get(id) is not { } node)
            return;
        node.Label.Shortcuts.Add(new WidgetShortcut(combo, action));
        _dirty = true;
    }

    /// <summary>Remove every shortcut from a widget.</summary>
    public void ClearShortcuts(NodeId id)
    {
        if (_tree.Get(id) is not { } node)
            return;
        node.Label.Shortcuts.Clear();
        _dirty = true;
    }

    /// <summary>When the focused node is no longer reachable (not
    /// focusable, or under a hidden ancestor), move focus to the nearest
    /// focusable node. True if focus moved.</summary>
    private bool RecoverUnreachableFocus()
    {
        var focused = _tree.Focus;
        if (focused.IsNone || Reachable(focused))
            return false;
        var parent = _tree.Parent(focused);
        var next = Nav.RecoverFocus(_tree, parent);
        if (!next.IsNone && next != focused)
        {
            LandFocus(next, FocusCause.Recovery);
            return true;
        }
        return false;
    }

    /// <summary>Whether the user can currently reach this node: focusable
    /// in itself (visible, focusable kind — disabled widgets stay
    /// reachable) and not inside a hidden subtree. Gates focus retention
    /// and recovery.</summary>
    private bool Reachable(NodeId id)
    {
        var node = _tree.Get(id);
        if (node is null || !node.Label.IsFocusableNow)
            return false;
        for (var parent = _tree.Parent(id); !parent.IsNone; parent = _tree.Parent(parent))
        {
            var p = _tree.Get(parent);
            if (p is not null && p.Label.Hidden)
                return false;
        }
        return true;
    }

    /// <summary>Reachable and enabled — able to act on the user's behalf.
    /// Gates primary/cancel activation.</summary>
    private bool Activatable(NodeId id) =>
        Reachable(id) && _tree.Get(id) is { } node && node.Label.IsInteractiveNow;

    /// <summary>The focused node's widget when it can act. A disabled
    /// widget keeps focus for discoverability but is inert: neither
    /// logical input nor key bindings reach it.</summary>
    public Widget? ActiveFocusOwner()
    {
        var node = _tree.Get(_tree.Focus);
        return node is not null && node.Label.IsInteractiveNow ? node.Owner : null;
    }

    // ── Focus ──

    /// <summary>Move focus programmatically. The tick end reads the
    /// landing - unless the node lives under an open dialog, in which
    /// case its layer's focus moves in silence: a result handler
    /// re-homing the ground before its dialog closes (deliver first,
    /// close second) is not yet the user's ground, and the pop reads
    /// the landing as the recovery it is, since it differs from the
    /// snapshot the layer was pushed over.</summary>
    public void SetFocus(NodeId id)
    {
        if (!_tree.Contains(id))
            return;
        if (_tree.InActiveLayer(id))
        {
            SetFocusInternal(id, FocusCause.Programmatic);
            return;
        }
        var old = _tree.FocusInLayerOf(id);
        if (old == id)
            return;
        var parent = _tree.Parent(old);
        if (!old.IsNone && !parent.IsNone)
            _focusMemory.Remember(parent, old);
        _tree.SetFocus(id);
        _dirty = true;
    }

    /// <summary>If nothing is focused, focus the first focusable node.
    /// Hosts call this once after building the initial UI.</summary>
    public bool EnsureFocus()
    {
        if (!_tree.Focus.IsNone)
            return false;
        var first = Nav.TabNext(_tree, NodeId.None);
        if (first.IsNone)
            return false;
        LandFocus(first, FocusCause.Programmatic);
        return true;
    }

    private void SetFocusInternal(NodeId next, FocusCause cause)
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
        LandFocus(next, cause);
    }

    /// <summary>Set focus and record why, for the tick end. The reshape
    /// hook runs now, so a widget reshaping its state on entry (an edit
    /// box selecting all) is described as reshaped.</summary>
    private void LandFocus(NodeId id, FocusCause cause)
    {
        _tree.SetFocus(id);
        _pendingCause = cause;
        _dirty = true;
        if (_tree.Get(id)?.Owner is Widget owner)
            owner.OnFocusGained();
    }

    /// <summary>Ask for the focused widget to be read in full at the
    /// tick end, as a re-announcement — speak-focus, or with its context
    /// labels after a view transition (a dialog opening), when the plain
    /// reading would lack orientation.</summary>
    public void RequestReread(bool withContextLabels)
    {
        _rereadAll = true;
        _contextLabels |= withContextLabels;
        _dirty = true;
    }

    /// <summary>The context spoken ahead of a focus arrival: the groups
    /// focus enters moving from <paramref name="from"/> to
    /// <paramref name="id"/>, outermost first — every group enclosing the
    /// target that does not also enclose (or is not) the origin; an
    /// unnamed group with the default role is structure, not context —
    /// then, when asked, the Label siblings preceding the widget.</summary>
    private List<ContextEntry> ContextFor(NodeId id, NodeId from, bool withLabels)
    {
        var result = new List<ContextEntry>();
        for (var ancestor = _tree.Parent(id); !ancestor.IsNone; ancestor = _tree.Parent(ancestor))
        {
            if (ancestor == from || IsAncestor(ancestor, from))
                break;
            var node = _tree.Get(ancestor);
            if (node is null || node.Label.Focusable || node.Label.IsContextLabel
                || node.Owner is not Widget group)
                continue;
            var name = group.Name ?? "";
            if (name.Length == 0 && ReferenceEquals(group.Role, Role.Group))
                continue;
            result.Insert(0, new ContextEntry(name.Length == 0 ? null : name, group.Role));
        }
        if (!withLabels)
            return result;
        var parent = _tree.Parent(id);
        IReadOnlyList<NodeId> siblings = parent.IsNone ? _tree.Roots : _tree.Children(parent);
        foreach (var sibling in siblings)
        {
            if (sibling == id)
                break;
            var node = _tree.Get(sibling);
            if (node is { Label.IsContextLabel: true, Owner: Widget label }
                && !string.IsNullOrEmpty(label.Name))
                result.Add(new ContextEntry(label.Name, Role.Label));
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
    /// navigable. The focused widget's fields are snapshotted for the
    /// delta comparison when the layer pops.</summary>
    public void PushLayer()
    {
        _layerSnapshots.Add(SnapshotFor(_tree.Focus));
        _tree.PushLayer();
        _dirty = true;
    }

    /// <summary>Pop the top layer. The previous layer's focus is
    /// restored; what the tick end describes is the verbosity's restore
    /// mode (<see cref="SpeechVerbosity.Restore"/>): the full reading by
    /// default, only the fields that changed since the layer was pushed
    /// under Changes - value and state if they moved (a rename landing,
    /// a listing refreshed), nothing when nothing did - and nothing at
    /// all under None. Only focus landing somewhere else - the
    /// pushed-from node is gone - always reads in full, as the recovery
    /// it is. <paramref name="announce"/> false pops without any of
    /// that - the intermediate layers of a cascade, which were never
    /// the user's ground.</summary>
    public void PopLayer(bool announce = true)
    {
        var snapshot = _layerSnapshots.Count > 0 ? _layerSnapshots[^1] : null;
        if (_layerSnapshots.Count > 0)
            _layerSnapshots.RemoveAt(_layerSnapshots.Count - 1);
        var restored = _tree.PopLayer();
        _focusMemory.Gc(_tree);
        _dirty = true;
        if (!announce || restored.IsNone)
            return;
        if (snapshot is not { } known || known.Focus != restored)
        {
            LandFocus(restored, FocusCause.Recovery);
            return;
        }
        // The reshape hook runs whatever the restore says.
        if (_tree.Get(restored)?.Owner is Widget owner)
            owner.OnFocusGained();
        _pendingCause = FocusCause.LayerRestore;
        _restoreBaseline = known;
    }

    /// <summary>What the focused widget was described as when a layer
    /// was pushed over it, for the delta comparison at pop.</summary>
    private readonly record struct LayerSnapshot(
        NodeId Focus, FieldSet Control, Element? Item, FieldSet? ItemFields);

    private readonly List<LayerSnapshot?> _layerSnapshots = new();

    private LayerSnapshot? SnapshotFor(NodeId id)
    {
        if (id.IsNone || _tree.Get(id) is not { Owner: Widget owner })
            return null;
        var item = owner.CurrentItem;
        return new LayerSnapshot(id, owner.Describe(), item, item?.Describe());
    }

    // ── Input dispatch ──

    /// <summary>Dispatch one logical input. Claim order: the focused
    /// node's widget first, then framework navigation and layer defaults,
    /// then widget shortcuts. True if consumed; the host routes unconsumed
    /// input to its own bindings.</summary>
    public bool HandleInput(in InputEvent input)
    {
        _dirty = true;
        // Establish focus if the tree has focusable content but no focus.
        if (_tree.Focus.IsNone)
        {
            var established = EnsureFocus();
            // A tab press that established focus is satisfied by it.
            if (established && input.Kind is InputKind.NavigateNext or InputKind.NavigatePrev)
                return true;
        }

        // 1. The focused widget gets first claim — unless disabled: a
        //    disabled widget keeps focus for discoverability but is inert.
        if (ActiveFocusOwner() is Widget owner && owner.HandleEngineInput(input))
            return true;

        // 2. Framework navigation and layer defaults.
        switch (input.Kind)
        {
            case InputKind.NavigateNext:
            {
                var next = Nav.TabNext(_tree, _tree.Focus);
                if (!next.IsNone)
                    SetFocusInternal(next, FocusCause.UserNavigation);
                return true;
            }
            case InputKind.NavigatePrev:
            {
                var prev = Nav.TabPrev(_tree, _tree.Focus);
                if (!prev.IsNone)
                    SetFocusInternal(prev, FocusCause.UserNavigation);
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
                    RequestReread(withContextLabels: false);
                return true;
            case InputKind.Activate:
                // A hidden or disabled primary does not activate; the
                // input falls through unconsumed.
                if (!_tree.Primary.IsNone && Activatable(_tree.Primary))
                {
                    Emit(new CoreEvent.Activated(_tree.Primary));
                    return true;
                }
                break;
            case InputKind.Dismiss:
                // Same for the cancel widget: unconsumed Dismiss lets the
                // host fall back (e.g. closing a dialog directly).
                if (!_tree.Cancel.IsNone && Activatable(_tree.Cancel))
                {
                    Emit(new CoreEvent.Activated(_tree.Cancel));
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
                SetFocusInternal(shortcut.Node, FocusCause.Shortcut);
            if (shortcut.Action is ShortcutAction.Activate or ShortcutAction.JumpAndActivate)
                Emit(new CoreEvent.Activated(shortcut.Node));
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
            SetFocusInternal(target, FocusCause.UserNavigation);
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
                SetFocusInternal(remembered, FocusCause.UserNavigation);
                return;
            }
        }
        var target = Nav.TreeNav(_tree, container, TreeDirection.Down);
        if (!target.IsNone)
            SetFocusInternal(target, FocusCause.UserNavigation);
    }

    // ── Output ──

    private static readonly List<CoreEvent> EmptyBatch = new();

    /// <summary>Drain the output queue in emission order. Empty drains
    /// return a shared list so the idle loop allocates nothing; treat
    /// the result as read-only.</summary>
    public List<CoreEvent> DrainEvents()
    {
        if (_events.Count == 0)
            return EmptyBatch;
        var batch = _events;
        _events = new List<CoreEvent>();
        return batch;
    }

    // ── The tick end ──

    // What the user last heard: the focused node at the end of the
    // previous tick, its fields, and the item under its cursor with its
    // fields. The tick end diffs the settled state against these.
    private NodeId _heardFocus = NodeId.None;
    private FieldSet? _heardControl;
    private Element? _heardItem;
    private FieldSet? _heardItemFields;

    // Requests accumulated during the tick.
    private FocusCause? _pendingCause;
    private bool _rereadAll;
    private bool _contextLabels;
    private LayerSnapshot? _restoreBaseline;
    private readonly List<Widget> _requesters = new();

    /// <summary>A widget registered a suppress or reread request for
    /// this tick; the engine clears it at the tick end.</summary>
    public void NoteTickRequest(Widget widget)
    {
        _dirty = true;
        if (!_requesters.Contains(widget))
            _requesters.Add(widget);
    }

    /// <summary>End the tick: drop the events of widgets other than the
    /// one focus settled on, describe that widget and the item under its
    /// cursor, and append what the user should now hear — a
    /// <see cref="AccessibilityEvent.FocusArrived"/> with every field
    /// when focus moved (or a reread was asked for), every item field
    /// when the cursor moved to another item, else the fields that
    /// changed since the user last heard them. Suppressed fields are
    /// left out of deltas; reread fields are put in whether or not they
    /// changed. Clears the dirty flag and every per-tick request.</summary>
    public void EndTick(List<AccessibilityEvent> tick)
    {
        _dirty = false;
        var focus = _tree.Focus;
        var owner = _tree.Get(focus)?.Owner;
        // A widget's events speak only where focus settled — on it, or
        // inside it: a container speaks for the children it holds — and
        // a widget that speaks through another is heard where that one
        // would be.
        if (tick.Count > 0)
            tick.RemoveAll(e => e.Source is not null && !Encloses(Credited(e.Source), owner));

        var cause = _pendingCause;
        var rereadAll = _rereadAll;
        var withLabels = _contextLabels;
        var restore = _restoreBaseline;
        _pendingCause = null;
        _rereadAll = false;
        _contextLabels = false;
        _restoreBaseline = null;

        if (owner is null)
        {
            _heardFocus = focus;
            _heardControl = null;
            _heardItem = null;
            _heardItemFields = null;
            ClearTickRequests();
            return;
        }

        var control = owner.Describe();
        var item = owner.CurrentItem;
        var itemFields = item?.Describe();

        var baseControl = _heardControl;
        var baseItemFields = ReferenceEquals(item, _heardItem) ? _heardItemFields : null;
        bool full;
        var arrival = cause ?? FocusCause.Programmatic;
        var from = _heardFocus;
        var moved = focus != _heardFocus;
        var speak = true;
        if (rereadAll)
        {
            full = true;
            arrival = FocusCause.Reannounce;
            from = NodeId.None;
        }
        else if (moved)
        {
            full = true;
            if (arrival == FocusCause.LayerRestore && restore is { } known)
            {
                switch (Verbosity.Restore)
                {
                    case RestoreAnnouncement.None:
                        speak = false;
                        break;
                    case RestoreAnnouncement.Changes:
                        full = false;
                        baseControl = known.Control;
                        baseItemFields = ReferenceEquals(item, known.Item) ? known.ItemFields : null;
                        break;
                }
            }
            if (arrival is FocusCause.Recovery or FocusCause.LayerRestore)
                from = NodeId.None;
        }
        else
        {
            full = false;
        }

        if (speak && full)
        {
            // The landing is the whole utterance: what the arriving
            // widget did on the way — the moves, the typing — is
            // superseded by its reading. What it deliberately said is
            // not: an announcement of its own is heard before it.
            if (moved && tick.Count > 0)
                tick.RemoveAll(e => e is not AccessibilityEvent.Announce && ReferenceEquals(e.Source, owner));
            tick.Add(new AccessibilityEvent.FocusArrived(owner, arrival,
                arrival == FocusCause.LayerRestore ? [] : ContextFor(focus, from, withLabels)));
            foreach (var (field, value) in control.Entries)
                tick.Add(new AccessibilityEvent.FieldValue(owner, field, value, FieldScope.Control));
            if (itemFields is not null)
                foreach (var (field, value) in itemFields.Entries)
                    tick.Add(new AccessibilityEvent.FieldValue(owner, field, value, FieldScope.Item));
        }
        else if (speak)
        {
            // The cursor landed on another item: the landed item reads
            // in full, with its position, after the control's deltas.
            var landed = itemFields is not null && baseItemFields is null && item is not null;
            EmitDeltas(tick, owner, control, baseControl, FieldScope.Control, landed);
            if (itemFields is not null)
            {
                if (landed)
                    tick.Add(new AccessibilityEvent.ItemArrived(owner, item!));
                EmitDeltas(tick, owner, itemFields, baseItemFields, FieldScope.Item, false);
            }
        }

        _heardFocus = focus;
        _heardControl = control;
        _heardItem = item;
        _heardItemFields = itemFields;
        ClearTickRequests();
    }

    /// <summary>The widget an event of <paramref name="source"/> is
    /// credited to: the end of its <see cref="Widget.SpeaksThrough"/>
    /// chain, or itself.</summary>
    private static Widget Credited(Widget source)
    {
        var credited = source;
        for (var hops = 0; credited.SpeaksThrough is { } through && hops < 16; hops++)
            credited = through;
        return credited;
    }

    /// <summary>Whether <paramref name="source"/> is <paramref name="focus"/>
    /// or one of its containers.</summary>
    private static bool Encloses(Widget source, Widget? focus)
    {
        for (var widget = focus; widget is not null; widget = widget.Parent)
            if (ReferenceEquals(widget, source))
                return true;
        return false;
    }

    private static void EmitDeltas(
        List<AccessibilityEvent> tick, Widget owner, FieldSet after, FieldSet? before,
        FieldScope scope, bool landed)
    {
        foreach (var (field, value) in after.Entries)
        {
            if (field.FocusOnly)
                continue;
            // An arrival reads in full, whatever the tick suppressed:
            // the fields of an item the cursor landed on (nothing was
            // heard of it before), and the position it landed at, which
            // belongs with it. A reread is the later, more deliberate
            // request: it wins over a suppression in the same tick.
            var arrival = before is null || (landed && ReferenceEquals(field, Fields.Position));
            if (arrival || owner.IsRereadRequested(field, scope))
            {
                tick.Add(new AccessibilityEvent.FieldValue(owner, field, value, scope));
                continue;
            }
            if (owner.IsSuppressed(field))
                continue;
            if (!before!.TryGetBoxed(field, out var old) || !field.ValuesEqual(old, value))
                tick.Add(new AccessibilityEvent.FieldValue(owner, field, value, scope));
        }
    }

    private void ClearTickRequests()
    {
        if (_requesters.Count == 0)
            return;
        foreach (var widget in _requesters)
            widget.ClearTickRequests();
        _requesters.Clear();
    }
}
