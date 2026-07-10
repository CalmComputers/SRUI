using System.Diagnostics;
using Srui.Audio;
using Srui.Core;

namespace Srui;

/// <summary>
/// The application shell: owns the engine, the readers, and (unless
/// headless) the window and speech output, and runs the event loop.
/// Widgets are created with the app (or another widget) as their
/// container; call <see cref="Run"/> when the tree is built. A headless
/// app (<see cref="Headless"/>) has no window and no voice — the host (a
/// test, or a platform embedding) pushes inputs through
/// <see cref="HandleInput"/> and drives <see cref="DispatchEvents"/> and
/// <see cref="SetNow"/> itself.
/// </summary>
public sealed class SruiApp : IWidgetContainer, IDisposable
{
    internal CoreUi Engine { get; } = new();

    /// <summary>The SDL window and event pump; null when headless.</summary>
    public SdlHost? Host { get; }

    private readonly SpeechReader? _speechReader;

    /// <summary>The Prism speech output behind the default speech reader;
    /// null when headless.</summary>
    public Speech? Voice => _speechReader?.Voice;

    private SoundManager? _audio;
    private uint _audioPeriodFrames;

    /// <summary>The audio device period in frames. The getter returns
    /// the period the device actually granted, creating the app-owned
    /// manager on first use exactly like <see cref="Audio"/>. The
    /// setter requests a new period: 0 selects the 128-frame
    /// low-latency default (~2.7ms at 48kHz); heavy scenes (hundreds
    /// of voices, many HRTF positions) buy mixing headroom with e.g.
    /// 512. Because the device and the HRTF convolvers are built
    /// around the period, setting it while the manager exists disposes
    /// the manager — and with it every Sound and SoundGroup — and the
    /// next <see cref="Audio"/> touch rebuilds at the new size; the
    /// application reloads its sounds exactly as at startup. Change it
    /// only at moments that tolerate a brief silence (startup, menus,
    /// an options apply). Re-assigning the current request is a
    /// no-op.</summary>
    public uint AudioPeriodFrames
    {
        get => Audio.DevicePeriodFrames;
        set
        {
            if (value == _audioPeriodFrames)
                return;
            _audioPeriodFrames = value;
            _audio?.Dispose();
            _audio = null;
        }
    }

    /// <summary>The audio device sample rate in Hz, creating the
    /// app-owned manager on first use exactly like
    /// <see cref="Audio"/>.</summary>
    public uint AudioSampleRate => Audio.SampleRate;

    /// <summary>Game audio, created on first use and owned by the app.
    /// The event loop advances its automation (pitch tweens,
    /// spatialization) every iteration — about 5ms at idle — so
    /// SoundManager.Tick never needs manual driving here. Consumers
    /// without an SruiApp use Srui.Audio standalone and tick their own
    /// loop.</summary>
    public SoundManager Audio => _audio ??= new SoundManager(_audioPeriodFrames);

    /// <summary>Called with input nothing consumed — the place for
    /// host-side key bindings. Return true when handled.</summary>
    public Func<InputEvent, bool>? UnhandledInput { get; set; }

    /// <summary>Called with physical key transitions (press, repeat,
    /// release) the focused widget's BindKey handlers didn't claim —
    /// the place for focus-independent game input. Return true when
    /// handled.</summary>
    public Func<KeyInput, bool>? UnhandledKey { get; set; }

    /// <summary>The window lost keyboard focus. Held-key releases will
    /// not arrive; zero any held-key state here.</summary>
    public Action? FocusLost { get; set; }

    /// <summary>A clean Alt tap (commonly a menu or palette).</summary>
    public Action? AltTap { get; set; }

    private readonly List<IReader> _readers = new();
    private readonly Dictionary<ulong, Ticker> _tickers = new();
    private readonly Stack<Dialog> _dialogs = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _running;

    SruiApp IWidgetContainer.App => this;

    /// <summary>A windowed app: SDL window, system clipboard, and a
    /// speech reader over Prism (the running screen reader, or platform
    /// TTS) installed out of the box.</summary>
    public SruiApp(string title, uint width = 400, uint height = 300)
    {
        Host = new SdlHost(title, width, height);
        Host.ProvideClipboard(this);
        _speechReader = new SpeechReader();
        _readers.Add(_speechReader);
    }

    private SruiApp()
    {
    }

    /// <summary>An app with no window, no clipboard, and no voice: the
    /// engine and widget layer only. For tests and custom hosts — attach
    /// readers, push inputs, drive the clock and the drain yourself.</summary>
    public static SruiApp Headless() => new();

    /// <summary>Install a platform clipboard (headless hosts; a windowed
    /// app already carries the system clipboard).</summary>
    public void SetClipboard(IClipboard clipboard) => Engine.SetClipboard(clipboard);

    public void Dispose()
    {
        _audio?.Dispose();
        _speechReader?.Dispose();
        Host?.Dispose();
    }

    // ── Readers ──

    /// <summary>Attach a reader: it receives every accessibility event
    /// the drain delivers, alongside the default speech reader.</summary>
    public void AddReader(IReader reader) => _readers.Add(reader);

    /// <summary>Detach a reader. True when it was attached.</summary>
    public bool RemoveReader(IReader reader) => _readers.Remove(reader);

    /// <summary>Tell every reader the user acted: speech silences,
    /// readers without a notion of interruption ignore it.</summary>
    public void Interrupt()
    {
        foreach (var reader in _readers)
            reader.OnInterrupt();
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
        // Dialogs close strictly LIFO; closing a buried dialog closes
        // those above it.
        while (_dialogs.Count > 0)
        {
            var top = _dialogs.Pop();
            Engine.PopLayer();
            if (ReferenceEquals(top, dialog))
                break;
            top.Close();
        }
    }

    // ── Tickers ──

    /// <summary>Start a periodic ticker; subscribe to its Tick event.
    /// Resolution is the SetNow cadence — in a windowed app, the event
    /// loop (~5ms).</summary>
    public Ticker StartTicker(uint intervalMs)
    {
        var ticker = new Ticker(this, Engine.AddTicker(intervalMs));
        _tickers[ticker.Id] = ticker;
        return ticker;
    }

    internal void StopTicker(Ticker ticker)
    {
        _tickers.Remove(ticker.Id);
        Engine.RemoveTicker(ticker.Id);
    }

    // ── Layer defaults, announcements ──

    /// <summary>Enter anywhere presses this widget (unless the focused
    /// widget claims Enter itself).</summary>
    public void SetPrimary(Widget widget) => Engine.SetPrimary(widget.Node);

    /// <summary>Escape anywhere presses this widget.</summary>
    public void SetCancel(Widget widget) => Engine.SetCancel(widget.Node);

    /// <summary>Queue a free-form announcement (polite: speaks after
    /// whatever is already being said). There is deliberately no urgent
    /// variant: a physical keypress already silences speech, and cutting
    /// speech off from a timer is bad screen-reader manners — use an
    /// earcon for asynchronous urgency.</summary>
    public void Announce(string text) => Engine.Announce(text);

    /// <summary>Re-announce the focused widget with its context labels
    /// (preceding Label siblings) — the dialog-open announcement.</summary>
    public void ReannounceWithContext() => Engine.ReannounceWithContext();

    /// <summary>Focus the first focusable widget if nothing is focused.</summary>
    public bool EnsureFocus() => Engine.EnsureFocus();

    // ── Driving the engine (Run does this; headless hosts do it themselves) ──

    /// <summary>Advance the engine clock (monotonic milliseconds):
    /// typeahead timeouts are observed and tickers fire from here.
    /// <see cref="Run"/> feeds it from its own stopwatch every iteration;
    /// call it yourself only when driving a headless app.</summary>
    public void SetNow(ulong milliseconds) => Engine.SetNow(milliseconds);

    /// <summary>Dispatch one logical input through the claim order:
    /// focused widget, framework navigation and layer defaults, widget
    /// shortcuts, then dialog dismissal and the UnhandledInput hook.
    /// False when nothing consumed it. Queued output is not delivered
    /// until <see cref="DispatchEvents"/> runs.</summary>
    public bool HandleInput(in InputEvent input)
    {
        if (Engine.HandleInput(input))
            return true;
        // Escape closes an open dialog with no explicit cancel widget.
        if (input.Kind == InputKind.Dismiss && _dialogs.Count > 0)
        {
            _dialogs.Peek().Dismiss();
            return true;
        }
        return UnhandledInput?.Invoke(input) == true;
    }

    /// <summary>Dispatch one physical key transition: the focused
    /// widget's BindKey handlers first (inert while it is disabled),
    /// then the UnhandledKey hook. False when nothing claimed it.</summary>
    public bool HandleKey(in KeyInput key)
    {
        if (Engine.ActiveFocusOwner()?.TryHandleKey(key) == true)
            return true;
        return UnhandledKey?.Invoke(key) == true;
    }

    /// <summary>Drain the engine until quiescent, delivering
    /// accessibility events to the readers and widget/tick notifications
    /// to their objects. Handlers may queue more output (announcements,
    /// dialogs); it is delivered in the same call.</summary>
    public void DispatchEvents()
    {
        while (true)
        {
            var batch = Engine.DrainEvents();
            if (batch.Count == 0)
                break;
            foreach (var ev in batch)
                Dispatch(ev);
        }
    }

    private void Dispatch(CoreEvent ev)
    {
        switch (ev)
        {
            case CoreEvent.Acc(var acc):
                foreach (var reader in _readers)
                    reader.OnEvent(acc);
                break;
            case CoreEvent.Activated(var node):
                Engine.OwnerOf(node)?.InvokeActivated();
                break;
            case CoreEvent.Callback(var invoke):
                invoke();
                break;
            case CoreEvent.Tick(var id):
                _tickers.GetValueOrDefault(id)?.OnTick();
                break;
        }
    }

    // ── The loop ──

    /// <summary>Stop the event loop after the current iteration.</summary>
    public void Quit() => _running = false;

    /// <summary>Run until <see cref="Quit"/> or the window closes.
    /// Requires a windowed app.</summary>
    public void Run()
    {
        if (Host is not SdlHost host)
            throw new InvalidOperationException(
                "a headless app has no event loop; drive HandleInput/DispatchEvents/SetNow yourself");
        Engine.EnsureFocus();
        _running = true;
        while (_running)
        {
            // Every iteration, not just on input: tickers fire from here,
            // and audio automation advances at the loop cadence.
            Engine.SetNow((ulong)_clock.ElapsedMilliseconds);
            _audio?.Tick();
            foreach (var hostEvent in host.Pump(5))
            {
                switch (hostEvent)
                {
                    case HostEvent.Quit:
                        _running = false;
                        break;
                    case HostEvent.KeyDown:
                        Interrupt();
                        break;
                    case HostEvent.AltTap:
                        AltTap?.Invoke();
                        break;
                    case HostEvent.Key(var keyInput):
                        HandleKey(keyInput);
                        break;
                    case HostEvent.FocusLost:
                        FocusLost?.Invoke();
                        break;
                    case HostEvent.Input(var input):
                        HandleInput(input);
                        break;
                }
            }
            DispatchEvents();
        }
    }
}
