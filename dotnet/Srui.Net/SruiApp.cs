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
    internal void Unregister(Widget widget) => _widgets.Remove(widget.Node);

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
                (Find(node) as Button)?.OnActivated();
                break;
            case OutputEvent.SecondaryActivated(var node):
                (Find(node) as Button)?.OnSecondaryActivated();
                break;
            case OutputEvent.Toggled(var node, var isChecked):
                (Find(node) as CheckBox)?.OnToggled(isChecked);
                break;
            case OutputEvent.Changed(var node):
                Find(node)?.OnChanged();
                break;
        }
    }

    private Widget? Find(NodeId node) => _widgets.GetValueOrDefault(node);
}
