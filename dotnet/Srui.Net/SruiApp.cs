using System.Diagnostics;

namespace Srui;

/// <summary>
/// The application shell: owns the window, the speech output, and the
/// Ui, runs the event loop, and routes output events to widget
/// instances. Widgets are created with the app (or a Group) as their
/// container; call <see cref="Run"/> when the tree is built.
/// </summary>
public sealed class SruiApp : IWidgetContainer, IDisposable
{
    /// <summary>The underlying context — the escape hatch for anything
    /// the class layer doesn't cover.</summary>
    public Ui Ui { get; }

    public SdlHost Host { get; }
    public Speech Voice { get; }

    /// <summary>Called with input nothing consumed — the place for
    /// host-side key bindings. Return true when handled.</summary>
    public Func<InputEvent, bool>? UnhandledInput { get; set; }

    /// <summary>A clean Alt tap (commonly a menu or palette).</summary>
    public Action? AltTap { get; set; }

    private readonly Dictionary<NodeId, Widget> _widgets = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _running;

    SruiApp IWidgetContainer.App => this;
    NodeId IWidgetContainer.ContainerNode => NodeId.None;

    public SruiApp(string title, uint width = 400, uint height = 300)
    {
        Host = new SdlHost(title, width, height);
        Voice = new Speech();
        Ui = new Ui();
        Host.ProvideClipboard(Ui);
    }

    public void Dispose()
    {
        Ui.Dispose();
        Voice.Dispose();
        Host.Dispose();
    }

    internal void Register(Widget widget) => _widgets[widget.Node] = widget;

    /// <summary>Drop a widget and every registered descendant (walked
    /// via the C#-side parent chain) from the event router.</summary>
    internal void UnregisterSubtree(Widget root)
    {
        var doomed = _widgets.Values.Where(w => IsSelfOrDescendant(w, root)).ToList();
        foreach (var widget in doomed)
            _widgets.Remove(widget.Node);
    }

    private static bool IsSelfOrDescendant(Widget widget, Widget root)
    {
        for (var current = widget; current is not null; current = current.Parent)
        {
            if (ReferenceEquals(current, root))
                return true;
        }
        return false;
    }

    /// <summary>Enter anywhere presses this widget (unless the focused
    /// widget claims Enter itself).</summary>
    public void SetPrimary(Widget widget) => Ui.SetPrimary(widget.Node);

    /// <summary>Escape anywhere presses this widget.</summary>
    public void SetCancel(Widget widget) => Ui.SetCancel(widget.Node);

    /// <summary>Queue a free-form announcement.</summary>
    public void Announce(string text) => Ui.Announce(text);

    /// <summary>Stop the event loop after the current iteration.</summary>
    public void Quit() => _running = false;

    /// <summary>Run until <see cref="Quit"/> or the window closes.</summary>
    public void Run()
    {
        Ui.EnsureFocus();
        _running = true;
        while (_running)
        {
            foreach (var hostEvent in Host.Pump(5))
            {
                switch (hostEvent)
                {
                    case HostEvent.Quit:
                        _running = false;
                        break;
                    case HostEvent.KeyDown:
                        Voice.Stop();
                        break;
                    case HostEvent.AltTap:
                        AltTap?.Invoke();
                        break;
                    case HostEvent.Input(var input):
                        Ui.SetNow((ulong)_clock.ElapsedMilliseconds);
                        if (!Ui.HandleInput(input))
                            UnhandledInput?.Invoke(input);
                        break;
                }
            }

            // Drain until quiescent: handlers may queue announcements
            // that must be spoken this iteration.
            while (_running)
            {
                var batch = Ui.Drain();
                if (batch.Count == 0) break;
                foreach (var output in batch)
                    Dispatch(output);
            }
        }
    }

    private void Dispatch(OutputEvent output)
    {
        switch (output)
        {
            case OutputEvent.Speech(var text, _, _):
                Voice.Speak(text);
                break;
            case OutputEvent.Activated(var node):
                Expect<Button>(node)?.OnActivated();
                break;
            case OutputEvent.SecondaryActivated(var node):
                Expect<Button>(node)?.OnSecondaryActivated();
                break;
            case OutputEvent.Toggled(var node, var isChecked):
                Expect<CheckBox>(node)?.OnToggled(isChecked);
                break;
            case OutputEvent.Changed(var node):
                _widgets.GetValueOrDefault(node)?.OnChanged();
                break;
        }
    }

    /// <summary>Null for nodes not registered with the class layer (the
    /// raw-Ui escape hatch); throws when a registered widget's type
    /// contradicts the event — that is protocol confusion, not a state
    /// to limp through.</summary>
    private T? Expect<T>(NodeId node) where T : Widget
    {
        var widget = _widgets.GetValueOrDefault(node);
        return widget switch
        {
            null => null,
            T typed => typed,
            _ => throw new InvalidOperationException(
                $"event for node {node.Value} expected {typeof(T).Name}, found {widget.GetType().Name}"),
        };
    }
}
