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

/// <summary>Anything a widget can be created inside: the app root or a
/// Group.</summary>
public interface IWidgetContainer
{
    SruiApp App { get; }
    NodeId ContainerNode { get; }
}

/// <summary>
/// Base class for widget wrappers. A widget owns a node handle; its
/// behavior lives in the native core. Extend by subclassing (override
/// the <c>On*</c> methods) or by composition (subscribe to the events —
/// the default <c>On*</c> implementations raise them).
/// </summary>
public abstract class Widget
{
    public SruiApp App { get; }
    public NodeId Node { get; protected init; }

    /// <summary>What this widget was created inside: a Group, a Dialog,
    /// or the app root. Used for ownership walks (subtree and dialog
    /// unregistration).</summary>
    internal IWidgetContainer Container { get; }

    /// <summary>The containing widget — the Group it was created in, or
    /// null at the app root or directly inside a dialog.</summary>
    public Widget? Parent => Container as Widget;

    protected Ui Ui => App.Ui;

    protected Widget(IWidgetContainer parent)
    {
        App = parent.App;
        Container = parent;
    }

    /// <summary>Whether this widget's container chain passes through (or
    /// is) the given container.</summary>
    internal bool IsInside(IWidgetContainer container)
    {
        for (IWidgetContainer current = Container; ; )
        {
            if (ReferenceEquals(current, container))
                return true;
            if (current is Widget widget)
                current = widget.Container;
            else
                return false;
        }
    }

    /// <summary>Register with the app's event router. Derived
    /// constructors call this after creating their node.</summary>
    protected void Register() => App.Register(this);

    public void Focus() => Ui.SetFocus(Node);

    public bool IsFocused => Ui.Focus == Node;

    /// <summary>Remove this widget's node (and subtree) from the tree.
    /// Descendant widgets are unregistered with it.</summary>
    public void Remove()
    {
        App.UnregisterSubtree(this);
        Ui.Remove(Node);
    }

    private bool _hidden;
    private bool _disabled;

    /// <summary>Hide/show this widget and its subtree. Focus recovers
    /// (with an announcement) if it was inside.</summary>
    public bool Hidden
    {
        get => _hidden;
        set
        {
            _hidden = value;
            Ui.SetHidden(Node, value);
        }
    }

    /// <summary>Enable/disable this widget. Focus recovers if it was here.</summary>
    public bool Disabled
    {
        get => _disabled;
        set
        {
            _disabled = value;
            Ui.SetDisabled(Node, value);
        }
    }

    private List<(uint Key, Mods Mods, KeyPhase Phase, Action Handler)>? _keyBindings;

    /// <summary>Bind a handler to a physical key transition delivered
    /// while this widget is focused — game-style input, independent of
    /// the logical input stream (a Press binding fires even for keys the
    /// widget consumes, e.g. letters in an edit box). Press and Repeat
    /// match the exact combo ("q", "shift+q" are distinct bindings);
    /// Release matches the key alone, so a modifier pressed mid-hold
    /// cannot orphan the release. Throws ArgumentException on an
    /// unparseable combo or a Release combo with modifiers.</summary>
    public void BindKey(string combo, KeyPhase phase, Action handler)
    {
        if (!Keys.TryParse(combo, out var key, out var mods))
            throw new ArgumentException($"unparseable combo \"{combo}\"", nameof(combo));
        if (phase == KeyPhase.Release && mods != Mods.None)
            throw new ArgumentException(
                $"release bindings match the key regardless of modifiers; bind the bare key instead of \"{combo}\"",
                nameof(combo));
        (_keyBindings ??= []).Add((key, mods, phase, handler));
    }

    /// <summary>Remove every handler bound to this combo and phase.
    /// Returns true when any was removed.</summary>
    public bool UnbindKey(string combo, KeyPhase phase)
    {
        if (_keyBindings is null || !Keys.TryParse(combo, out var key, out var mods))
            return false;
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

    /// <summary>Attach a shortcut ("ctrl+shift+s" config form): pressing
    /// it jumps to this widget, activates it, or both. A widget may carry
    /// any number of shortcuts; the first added is the one focus
    /// announcements speak. A hidden or disabled widget's shortcuts are
    /// inert. Returns false when the combo fails to parse.</summary>
    public bool AddShortcut(string combo, ShortcutAction action = ShortcutAction.Jump) =>
        Ui.AddShortcut(Node, combo, action);

    /// <summary>Remove every shortcut from this widget.</summary>
    public void ClearShortcuts() => Ui.ClearShortcuts(Node);

    /// <summary>Rename the widget; re-announces when focused.</summary>
    public void SetName(string name) => Ui.SetNodeName(Node, name);

    /// <summary>Change the widget's spoken description.</summary>
    public void SetDescription(string description) => Ui.SetNodeDescription(Node, description);

    /// <summary>The widget's state changed (text edited, selection moved,
    /// slider adjusted, tab switched, combo captured).</summary>
    public event Action? Changed;

    protected internal virtual void OnChanged() => Changed?.Invoke();
}
