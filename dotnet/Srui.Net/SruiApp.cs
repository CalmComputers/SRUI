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
    private readonly Dictionary<ulong, Ticker> _tickers = new();
    private readonly Stack<Dialog> _dialogs = new();
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
    /// via the C#-side container chain) from the event router.</summary>
    internal void UnregisterSubtree(Widget root)
    {
        var doomed = _widgets.Values
            .Where(w => ReferenceEquals(w, root) || (root is IWidgetContainer c && w.IsInside(c)))
            .ToList();
        foreach (var widget in doomed)
            _widgets.Remove(widget.Node);
    }

    // ── Dialogs ──

    /// <summary>Open a modal dialog: a fresh layer that widgets are then
    /// created into. Focus it and call AnnounceOpened when built (the
    /// canned dialogs in SruiDialogs do all of this).</summary>
    public Dialog OpenDialog()
    {
        var dialog = new Dialog(this);
        _dialogs.Push(dialog);
        return dialog;
    }

    internal void CloseDialog(Dialog dialog)
    {
        // Unregister the dialog's widgets, pop its layer. Dialogs close
        // strictly LIFO; closing a buried dialog closes those above it.
        while (_dialogs.Count > 0)
        {
            var top = _dialogs.Pop();
            var doomed = _widgets.Values.Where(w => w.IsInside(top)).ToList();
            foreach (var widget in doomed)
                _widgets.Remove(widget.Node);
            Ui.PopLayer();
            if (ReferenceEquals(top, dialog))
                break;
            top.Close();
        }
    }

    // ── Tickers ──

    /// <summary>Start a periodic ticker; subscribe to its Tick event.
    /// Resolution is the event-loop cadence (~5ms).</summary>
    public Ticker StartTicker(uint intervalMs)
    {
        var ticker = new Ticker(this, Ui.AddTicker(intervalMs));
        _tickers[ticker.Id] = ticker;
        return ticker;
    }

    internal void StopTicker(Ticker ticker)
    {
        _tickers.Remove(ticker.Id);
        Ui.RemoveTicker(ticker.Id);
    }

    /// <summary>Enter anywhere presses this widget (unless the focused
    /// widget claims Enter itself).</summary>
    public void SetPrimary(Widget widget) => Ui.SetPrimary(widget.Node);

    /// <summary>Escape anywhere presses this widget.</summary>
    public void SetCancel(Widget widget) => Ui.SetCancel(widget.Node);

    /// <summary>Queue a free-form announcement (polite: speaks after
    /// whatever is already being said).</summary>
    public void Announce(string text) => Ui.Announce(text);

    /// <summary>Announce urgently: silences current and queued speech
    /// first. For time-critical game events, not routine feedback.</summary>
    public void AnnounceNow(string text)
    {
        Voice.Stop();
        Ui.Announce(text);
    }

    /// <summary>Stop the event loop after the current iteration.</summary>
    public void Quit() => _running = false;

    /// <summary>Run until <see cref="Quit"/> or the window closes.</summary>
    public void Run()
    {
        Ui.EnsureFocus();
        _running = true;
        while (_running)
        {
            // Every iteration, not just on input: tickers fire from here.
            Ui.SetNow((ulong)_clock.ElapsedMilliseconds);
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
                        if (!Ui.HandleInput(input))
                        {
                            // Escape closes an open dialog with no
                            // explicit cancel widget.
                            if (input.Kind == InputKind.Dismiss && _dialogs.Count > 0)
                                _dialogs.Peek().Dismiss();
                            else
                                UnhandledInput?.Invoke(input);
                        }
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
            case OutputEvent.Tick(var id):
                _tickers.GetValueOrDefault(id)?.OnTick();
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
