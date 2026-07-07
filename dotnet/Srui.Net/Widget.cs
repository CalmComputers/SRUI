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

    /// <summary>The containing widget — the Group it was created in, or
    /// null at the app root. Used for subtree unregistration.</summary>
    public Widget? Parent { get; }

    protected Ui Ui => App.Ui;

    protected Widget(IWidgetContainer parent)
    {
        App = parent.App;
        Parent = parent as Widget;
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
