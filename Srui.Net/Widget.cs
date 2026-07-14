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
/// and its input behavior. Extend by composition (subscribe to the
/// events), by subclassing a built-in widget (override the <c>On*</c>
/// methods, keep the base call so composition subscribers still fire), or
/// by authoring a new widget kind from this base: override
/// <see cref="OnInput"/> to claim input, expose the label's derived
/// fields as functions of widget state
/// (<see cref="ValueText"/>/<see cref="StateText"/>), and describe what
/// the user should perceive with <see cref="Announce"/>/
/// <see cref="AnnounceItem"/>/<see cref="Promulgate"/>. A widget authored
/// that way is a full citizen:
/// tab ring, focus recovery, dialog layers, shortcuts, and readers all
/// apply to it exactly as to the built-ins.
/// </summary>
public abstract class Widget : IWidgetContainer
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
    /// <paramref name="roleText"/> is the spoken role ("button", "table");
    /// empty announces role-less. Widgets created with
    /// <paramref name="focusable"/> false (labels, groups) are skipped by
    /// the tab ring and focus recovery, though hierarchy navigation can
    /// still land on them.</summary>
    protected Widget(IWidgetContainer parent, string? name, string roleText = "", bool focusable = true)
        : this(parent, name, roleText, focusable, isContextLabel: false)
    {
    }

    private protected Widget(
        IWidgetContainer parent, string? name, string roleText, bool focusable, bool isContextLabel)
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
        var label = new WidgetLabel(name, roleText)
        {
            Focusable = focusable,
            IsContextLabel = isContextLabel,
        };
        Node = Engine.Insert(parentNode, label, this);
    }

    // ── Focus and lifetime ──

    public void Focus() => Engine.SetFocus(Node);

    public bool IsFocused => Engine.Focus == Node;

    /// <summary>Remove this widget's node (and everything created inside
    /// it) from the tree. Focus recovers (with an announcement) if it was
    /// inside. The object stays readable but mutations no longer land
    /// anywhere.</summary>
    public void Remove() => Engine.Remove(Node);

    // ── The golden six as properties ──

    /// <summary>The widget's spoken name; setting speaks the new name
    /// when focused. Null announces as role and value only.</summary>
    public string? Name
    {
        get => Label?.Name;
        set => Engine.UpdateLabel(Node, label => label.Name = value);
    }

    /// <summary>Rename without the focused-delta announcement — for
    /// subclass handlers whose flow speaks the transition itself (a
    /// directory pane adopting the folder it navigated into). The new
    /// name still reads on every later announcement.</summary>
    protected void SetNameSilently(string? name) =>
        Engine.UpdateLabelSilently(Node, label => label.Name = name);

    /// <summary>The widget's spoken description; setting speaks the new
    /// description when focused. For explaining an esoteric name — never
    /// for keys or actions, which belong in <see cref="KeyHelp"/>
    /// (docs/accessibility-guidelines.md, section 4).</summary>
    public string Description
    {
        get => Label?.Description ?? "";
        set => Engine.UpdateLabel(Node, label => label.Description = value);
    }

    /// <summary>Hide/show this widget and its subtree. Focus recovers
    /// (with an announcement) if it was inside.</summary>
    public bool Hidden
    {
        get => Label is { } l && (l.States & WidgetStates.Hidden) != 0;
        set => Engine.SetHidden(Node, value);
    }

    /// <summary>Enable/disable this widget. A disabled widget stays in
    /// the tab ring — discoverable, announced "unavailable" — but is
    /// inert: input, key bindings, shortcuts, and primary/cancel
    /// activation all pass it by. Toggling while focused speaks the
    /// transition ("unavailable", "available") and focus stays put.</summary>
    public bool Disabled
    {
        get => Label is { } l && (l.States & WidgetStates.Disabled) != 0;
        set => Engine.SetState(Node, WidgetStates.Disabled, value);
    }

    /// <summary>Spoken as "required" in the focus announcement; toggling
    /// while focused speaks the transition ("required", "not required").</summary>
    public bool Required
    {
        get => Label is { } l && (l.States & WidgetStates.Required) != 0;
        set => Engine.SetState(Node, WidgetStates.Required, value);
    }

    /// <summary>Spoken as "warning" in the focus announcement; toggling
    /// while focused speaks the transition ("warning", "warning cleared").</summary>
    public bool Warning
    {
        get => Label is { } l && (l.States & WidgetStates.Warning) != 0;
        set => Engine.SetState(Node, WidgetStates.Warning, value);
    }

    private string? _keyHelp;

    /// <summary>Help text for the widget's extra keys and actions — the
    /// home for anything a user could not predict from the name and role
    /// (a list whose left and right arrows set priority, a game widget's
    /// key layout). A widget with help announces "with help" and shows the
    /// text in a reviewable status dialog on F1, which
    /// <see cref="ReservesKey"/> then reports reserved. Null (the default)
    /// removes the state and the F1 claim. Not a second description: text
    /// the user needs on every focus visit belongs in the label fields.</summary>
    public string? KeyHelp
    {
        get => _keyHelp;
        set
        {
            _keyHelp = value;
            Engine.SetState(Node, WidgetStates.WithHelp, value is not null);
        }
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

    /// <summary>The widget's state changed (text edited, selection moved,
    /// slider adjusted, tab switched, combo captured).</summary>
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
    /// state (the label follows by itself — <see cref="ValueText"/> and
    /// <see cref="StateText"/> are functions of it), describe the
    /// perceptual result with <see cref="Announce"/>/
    /// <see cref="AnnounceItem"/>/<see cref="Promulgate"/>, and defer
    /// program notifications with <see cref="Post"/>/
    /// <see cref="PostChanged"/> — handlers run after dispatch settles,
    /// so they may freely open dialogs or remove widgets.</summary>
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

    /// <summary>The label's value field — the third of the golden six (a
    /// list's selected item, an editor's current line). A function of
    /// widget state, never stored: the framework pulls it whenever an
    /// announcement or snapshot needs it, so it can never go stale.
    /// Override with a cheap, side-effect-free computation over your own
    /// state; the base has no value.</summary>
    protected internal virtual string ValueText => "";

    /// <summary>The label's dynamic state text, spoken before the flag
    /// states in focus announcements ("no filter"). A function of widget
    /// state, pulled like <see cref="ValueText"/>.</summary>
    protected internal virtual string StateText => "";

    /// <summary>Change the spoken role text; speaks the new role text
    /// when focused.</summary>
    protected void SetRoleText(string roleText) =>
        Engine.UpdateLabel(Node, label => label.RoleText = roleText);

    // Out to the user: the Announce family (Promulgate is the raw form).

    /// <summary>Queue a free-form announcement attributed to this widget
    /// (polite: speaks after whatever is already queued).</summary>
    protected void Announce(string text) =>
        Promulgate(new AccessibilityEvent.Announce(text, this));

    /// <summary>Queue an item-navigation event for this widget: the
    /// selected item's text, its position ((index, total), or null when
    /// the widget has no indexable concept), the boundary that was hit,
    /// if any, and — for multi-select widgets — the item's checked state
    /// (spoken as "checked" when true, nothing when false; null for
    /// widgets with no checked concept).</summary>
    protected void AnnounceItem(
        string item, (int Index, int Total)? position, Boundary? boundary, bool? isChecked = null) =>
        Promulgate(new AccessibilityEvent.ItemNav(this, item, position, boundary, isChecked));

    /// <summary>Queue a structured accessibility event for the readers —
    /// the raw form under the Announce family, for events the
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
