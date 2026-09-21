using Srui.Core;

namespace Srui;

/// <summary>What pressing a widget shortcut does.</summary>
public enum ShortcutAction
{
    /// <summary>Move focus to the widget (the mnemonic behavior).</summary>
    Jump = 0,
    /// <summary>Raise the widget's Activated event without moving focus.</summary>
    Activate = 1,
    /// <summary>Move focus to the widget, then activate it.</summary>
    JumpAndActivate = 2,
}

/// <summary>Anything a widget can be created inside: the app root, a
/// Dialog, or any other widget (a Group by convention). Implemented by
/// SruiApp, Dialog, and Widget itself; nothing else can be a container.</summary>
public interface IWidgetContainer
{
    SruiApp App { get; }
}

/// <summary>
/// Base class for widgets. A widget IS its node in the semantic tree:
/// one object holds the application-facing surface, the widget's state,
/// and its input behavior. What the user hears about it is its fields
/// (<see cref="Element"/>): declare them as [Field] properties, change
/// them freely, and the tick end speaks the difference between what the
/// user last heard and what is now under the cursor — nothing in a
/// widget decides when to speak state. Extend by composition (subscribe
/// to the events), by subclassing a built-in widget (override the
/// <c>On*</c> methods, keep the base call so composition subscribers
/// still fire), or by authoring a new widget kind from this base:
/// override <see cref="OnInput"/> to claim input, mutate your fields,
/// and emit action events (<see cref="Announce"/>, <see cref="Promulgate"/>)
/// for what is not state — an edge hit, a refusal. A widget authored
/// that way is a full citizen: tab ring, focus recovery, dialog layers,
/// shortcuts, and readers all apply to it exactly as to the built-ins.
/// </summary>
public abstract partial class Widget : Element, IWidgetContainer
{
    public SruiApp App { get; }

    /// <summary>The engine handle this widget embodies. Identity on the
    /// public surface is the object reference; the handle never leaves
    /// the assembly.</summary>
    internal NodeId Node { get; }

    /// <summary>What this widget was created inside: a widget, a Dialog,
    /// or the app root.</summary>
    internal IWidgetContainer Container { get; }

    /// <summary>The containing widget — the Group (or other widget) it
    /// was created in, or null at the app root or directly inside a
    /// dialog.</summary>
    public Widget? Parent => Container as Widget;

    internal CoreUi Engine => App.Engine;

    private WidgetLabel? Label => Engine.Label(Node);

    /// <summary>Create the widget's node under the container.
    /// <paramref name="role"/> is the widget's kind (<see cref="Srui.Role.Button"/>,
    /// a program's own); null is role-less. Widgets created with
    /// <paramref name="focusable"/> false (labels, groups) are skipped by
    /// the tab ring and focus recovery, though hierarchy navigation can
    /// still land on them.</summary>
    protected Widget(IWidgetContainer parent, string? name, Role? role = null, bool focusable = true)
        : this(parent, name, role, focusable, isContextLabel: false)
    {
    }

    private protected Widget(
        IWidgetContainer parent, string? name, Role? role, bool focusable, bool isContextLabel)
    {
        App = parent.App;
        Container = parent;
        var parentNode = parent switch
        {
            Widget widget => widget.Node,
            SruiApp or Dialog => NodeId.None,
            _ => throw new ArgumentException(
                "widgets can only be created inside an SruiApp, a Dialog, or another widget",
                nameof(parent)),
        };
        Node = Engine.Insert(parentNode, new WidgetLabel(focusable, isContextLabel), this);
        Name = name;
        Role = role ?? Role.None;
        Description = "";
    }

    // ── Focus and lifetime ──

    public void Focus() => Engine.SetFocus(Node);

    public bool IsFocused => Engine.Focus == Node;

    /// <summary>Focus just landed on this widget, before the tick end
    /// describes it — a hook for state the widget reshapes on entry
    /// (see <see cref="EditBox.SelectAllOnFocus"/>).</summary>
    protected internal virtual void OnFocusGained()
    {
    }

    /// <summary>Remove this widget's node (and everything created inside
    /// it) from the tree. Focus recovers if it was inside. The object
    /// stays readable but mutations no longer land anywhere.</summary>
    public void Remove() => Engine.Remove(Node);

    /// <summary>The widget this one's events are heard as if they came
    /// from. A widget's action events and announcements reach the user
    /// only where focus settled — on it or inside it; a widget that
    /// acts on another's behalf names that other here, and is then
    /// heard wherever it would be. A console's input box, run by keys
    /// typed while the user reads the output beside it, speaks through
    /// the pane holding both, so its echo is heard from the output.
    /// Null (the default) is the widget's own place.</summary>
    public Widget? SpeaksThrough { get; set; }

    // ── Fields every widget has ──

    /// <summary>The spoken name. Null announces as role and value only.</summary>
    [Field] public partial string? Name { get; set; }

    /// <summary>The widget's kind. Readers map it to a word.</summary>
    [Field] public partial Role Role { get; set; }

    /// <summary>Explanatory text for an esoteric name — never keys or
    /// actions, which belong in <see cref="KeyHelp"/>
    /// (docs/accessibility-guidelines.md, section 4).</summary>
    [Field] public partial string Description { get; set; }

    /// <summary>The shortcuts attached to this widget, first added first.</summary>
    [Field]
    public IReadOnlyList<KeyCombo> Shortcuts
    {
        get
        {
            var shortcuts = Label?.Shortcuts;
            if (shortcuts is null || shortcuts.Count == 0)
                return EmptyShortcuts;
            var result = new KeyCombo[shortcuts.Count];
            for (var i = 0; i < result.Length; i++)
                result[i] = shortcuts[i].Combo;
            return result;
        }
    }

    private static readonly KeyCombo[] EmptyShortcuts = [];

    /// <summary>Hide/show this widget and its subtree. Focus recovers
    /// if it was inside; the recovery is what the user hears.</summary>
    public bool Hidden
    {
        get => Label?.Hidden == true;
        set => Engine.SetHidden(Node, value);
    }

    /// <summary>Enable/disable this widget. A disabled widget stays in
    /// the tab ring — discoverable, announced "unavailable" — but is
    /// inert: input, key bindings, shortcuts, and primary/cancel
    /// activation all pass it by.</summary>
    [Field]
    public bool Disabled
    {
        get => Label?.Disabled == true;
        set => Engine.SetDisabled(Node, value);
    }

    /// <summary>Spoken as "required" by the speech reader.</summary>
    [Field] public partial bool Required { get; set; }

    /// <summary>Spoken as "warning" by the speech reader.</summary>
    [Field] public partial bool Warning { get; set; }

    private string? _keyHelp;
    private bool _announceHelp = true;

    /// <summary>Whether the widget carries key help and says so.</summary>
    [Field] public bool WithHelp => _keyHelp is not null && _announceHelp;

    /// <summary>Help text for the widget's extra keys and actions — the
    /// home for anything a user could not predict from the name and role
    /// (a list whose left and right arrows set priority, a game widget's
    /// key layout). A widget with help announces "with help" (unless
    /// <see cref="AnnounceHelp"/> opts out) and shows the
    /// text in a reviewable status dialog on F1, which
    /// <see cref="ReservesKey"/> then reports reserved. Null (the default)
    /// removes the state and the F1 claim. Not a second description: text
    /// the user needs on every focus visit belongs in the fields.</summary>
    public string? KeyHelp
    {
        get => _keyHelp;
        set
        {
            _keyHelp = value;
            Engine.Touch();
        }
    }

    /// <summary>Whether carrying key help is reflected in announcements
    /// (the "with help" state). False keeps the F1 dialog and its key
    /// reservation but drops the spoken state — for apps where help is
    /// so ubiquitous that the phrase would ride every focus visit and
    /// carry no information.</summary>
    public bool AnnounceHelp
    {
        get => _announceHelp;
        set
        {
            _announceHelp = value;
            Engine.Touch();
        }
    }

    /// <summary>The item under this widget's cursor, whose fields are
    /// read with the widget's as its item scope: a list's selected item,
    /// a tree's cursor node, a tab control's active tab. Null for
    /// widgets without a cursor. When it changes to another item, the
    /// tick end reads the landed item in full.</summary>
    protected internal virtual Element? CurrentItem => null;

    /// <summary>The engine's dirty flag: a field write on any widget may
    /// have changed what the user perceives.</summary>
    protected override void OnFieldWritten(Field field) => Touch();

    /// <summary>Tell the engine something may have changed — for a
    /// widget whose computed fields read private state it just moved
    /// (a cell cursor), so the loop's next tick end describes it. Field
    /// writes through generated properties do this by themselves.</summary>
    protected void Touch() => Engine.Touch();

    // ── Per-tick requests ──

    private HashSet<Field>? _suppressed;
    private bool _suppressAll;
    private HashSet<Field>? _reread;
    private bool _rereadItem;

    /// <summary>Keep changes in these fields of this widget out of this
    /// tick's reading (no fields means all of them). For the rare
    /// widget whose own action events are the better voice for a
    /// change — an edit box speaking the character typed rather than
    /// the line it changed. An arrival - focus landing here, or a
    /// re-announcement - discards what was asked before it, as it
    /// discards the widget's action events, and reads in full; asked
    /// after it, this trims the arrival itself, for a fact whatever
    /// brought the user here already said (a window title that named
    /// the folder a list is named for). Cleared at the tick end.</summary>
    public void Suppress(params Field[] fields)
    {
        if (fields.Length == 0)
            _suppressAll = true;
        else
            foreach (var field in fields)
                (_suppressed ??= new HashSet<Field>(ReferenceEqualityComparer.Instance)).Add(field);
        Engine.NoteTickRequest(this);
    }

    /// <summary>Put these fields into this tick's reading whether or
    /// not they changed — a slider at its edge re-saying its number.
    /// Cleared at the tick end.</summary>
    public void Reread(params Field[] fields)
    {
        foreach (var field in fields)
            (_reread ??= new HashSet<Field>(ReferenceEqualityComparer.Instance)).Add(field);
        Engine.NoteTickRequest(this);
    }

    /// <summary>Put every field of the item under the cursor, and the
    /// cursor's <see cref="Fields.Position"/>, into this tick's reading
    /// — the answer to an edge hit or a typeahead miss, where the user
    /// needs to hear where they still are.</summary>
    public void RereadItem()
    {
        _rereadItem = true;
        Engine.NoteTickRequest(this);
    }

    internal bool IsSuppressed(Field field) =>
        _suppressAll || _suppressed?.Contains(field) == true;

    internal bool IsRereadRequested(Field field, FieldScope scope) =>
        (_rereadItem && (scope == FieldScope.Item || Fields.IsPlace(field)))
        || _reread?.Contains(field) == true;

    internal bool HasRereadRequest => _rereadItem || _reread is { Count: > 0 };

    internal void ClearTickRequests()
    {
        _suppressed?.Clear();
        _suppressAll = false;
        _reread?.Clear();
        _rereadItem = false;
    }

    // ── Physical key bindings (the game-input stream) ──

    private List<(uint Key, Mods Mods, KeyPhase Phase, Action Handler)>? _keyBindings;

    /// <summary>Bind a handler to a physical key transition delivered
    /// while this widget is focused — game-style input, independent of
    /// the logical input stream (a Press binding fires even for keys the
    /// widget consumes, e.g. letters in an edit box). Bindings are inert
    /// while the widget is disabled, like its shortcuts. Press and Repeat
    /// match the exact combo (plain q and shift+q are distinct bindings);
    /// Release matches the key alone, so a modifier pressed mid-hold
    /// cannot orphan the release. Throws ArgumentException on a Release
    /// combo with modifiers.</summary>
    public void BindKey(KeyCombo combo, KeyPhase phase, Action handler)
    {
        var (key, mods) = combo.ToFlat();
        if (phase == KeyPhase.Release && mods != Mods.None)
            throw new ArgumentException(
                $"release bindings match the key regardless of modifiers; bind the bare key instead of \"{combo.ToConfigString()}\"",
                nameof(combo));
        (_keyBindings ??= []).Add((key, mods, phase, handler));
    }

    /// <summary>Remove every handler bound to this combo and phase.
    /// Returns true when any was removed.</summary>
    public bool UnbindKey(KeyCombo combo, KeyPhase phase)
    {
        if (_keyBindings is null)
            return false;
        var (key, mods) = combo.ToFlat();
        return _keyBindings.RemoveAll(b =>
            b.Phase == phase && b.Key == key && (phase == KeyPhase.Release || b.Mods == mods)) > 0;
    }

    /// <summary>Dispatch a key transition to this widget's bindings.
    /// True when any handler fired.</summary>
    internal bool TryHandleKey(in KeyInput input)
    {
        if (_keyBindings is null)
            return false;
        var handled = false;
        // Snapshot: handlers may bind/unbind while we iterate.
        foreach (var b in _keyBindings.ToArray())
        {
            if (b.Phase != input.Phase)
                continue;
            var matches = input.Phase == KeyPhase.Release
                ? b.Key == input.Key
                : b.Key == input.Key && b.Mods == input.Mods;
            if (matches)
            {
                b.Handler();
                handled = true;
            }
        }
        return handled;
    }

    // ── Shortcuts ──

    /// <summary>Attach a shortcut: pressing the combo jumps to this
    /// widget, activates it, or both. A widget may carry any number of
    /// shortcuts; the first added is the one focus announcements speak.
    /// A hidden or disabled widget's shortcuts are inert.</summary>
    public void AddShortcut(KeyCombo combo, ShortcutAction action = ShortcutAction.Jump) =>
        Engine.AddShortcut(Node, combo, action);

    /// <summary>Remove every shortcut from this widget.</summary>
    public void ClearShortcuts() => Engine.ClearShortcuts(Node);

    /// <summary>Whether this widget might consume the combo during normal
    /// interaction while focused — the soft-conflict side of bind-dialog
    /// warnings (<see cref="KeyCombo.ReservedReason"/> is the hard side).
    /// Widget kinds with input behavior override this to name their keys
    /// and keep the base call: the base claims F1 while
    /// <see cref="KeyHelp"/> is set.</summary>
    public virtual bool ReservesKey(KeyCombo combo) =>
        _keyHelp is not null && combo == KeyCombo.Plain(Key.F(1));

    // ── Events ──

    /// <summary>The user changed the widget's state (text edited,
    /// selection moved, slider adjusted, tab switched, combo captured).
    /// A program notification, not a speech mechanism: what the user
    /// hears is the tick end's own affair.</summary>
    public event Action? Changed;

    /// <summary>The widget was activated: pressed directly, triggered as
    /// the layer's primary or cancel widget, or fired by an Activate
    /// shortcut.</summary>
    public event Action? Activated;

    protected virtual void OnChanged() => Changed?.Invoke();

    protected virtual void OnActivated() => Activated?.Invoke();

    internal void InvokeActivated() => OnActivated();

    // ── Behavior authoring ──

    /// <summary>Handle a logical input directed at this widget while it
    /// is focused; the focused widget gets first claim, before framework
    /// navigation and shortcuts. Return true to consume. Mutate your own
    /// fields (the tick end speaks what changed), emit action events for
    /// what is not state (<see cref="Announce"/>, <see cref="Promulgate"/>,
    /// <see cref="RereadItem"/> on an edge), and defer program
    /// notifications with <see cref="Post"/>/<see cref="PostChanged"/> —
    /// handlers run after dispatch settles, so they may freely open
    /// dialogs or remove widgets.</summary>
    protected virtual bool OnInput(in InputEvent input) => false;

    internal bool HandleEngineInput(in InputEvent input)
    {
        if (OnInput(input))
            return true;
        // F1 reads the widget's key help, after the widget's own claim so
        // a subclass that uses F1 itself wins.
        if (_keyHelp is string help && input.Is(Key.F(1)))
        {
            Post(() => App.ShowStatus("Help", help));
            return true;
        }
        return false;
    }

    // Out to the user: action events. State is never emitted; the tick
    // end reads it.

    /// <summary>Queue a free-form announcement attributed to this widget.
    /// It speaks, in order with the tick's other announcements and
    /// before its state, only when this widget is where focus settles
    /// at the tick end — so a confirmation that moves focus to its
    /// result is heard as the result, not twice.</summary>
    protected void Announce(string text) =>
        Promulgate(new AccessibilityEvent.Announce(text, this));

    /// <summary>Queue an edge hit: navigation had nowhere to go. The
    /// item under the cursor is reread alongside, so the reader can say
    /// "top, Apple".</summary>
    protected void AnnounceBoundary(Boundary edge)
    {
        Promulgate(new AccessibilityEvent.BoundaryHit(this, edge));
        RereadItem();
    }

    /// <summary>Queue a structured accessibility event for the readers —
    /// the raw form under <see cref="Announce"/>, for action events the
    /// conveniences don't cover.</summary>
    protected void Promulgate(AccessibilityEvent e) => Engine.EmitAccessibility(e);

    // Into the program at drain time: the Post family.

    /// <summary>Defer a program notification to drain time, after input
    /// dispatch has settled. This is how a widget raises its own
    /// app-facing events: capture the payload, post the callback.</summary>
    protected void Post(Action callback) => Engine.Emit(new CoreEvent.Callback(callback));

    /// <summary>Post the Changed notification.</summary>
    protected void PostChanged() => Post(OnChanged);

    /// <summary>Post this widget's activation: the Activated event is
    /// raised when the host drains, exactly as for a press.</summary>
    protected void PostActivated() => Engine.Emit(new CoreEvent.Activated(Node));

    /// <summary>The host clock (monotonic milliseconds, advanced every
    /// loop iteration) — for typeahead-style timeouts.</summary>
    protected ulong NowMs => Engine.Now;
}
