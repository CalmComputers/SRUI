namespace Srui;

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

    /// <summary>The widget's state changed (text edited, selection moved,
    /// slider adjusted, tab switched, combo captured).</summary>
    public event Action? Changed;

    protected internal virtual void OnChanged() => Changed?.Invoke();
}
