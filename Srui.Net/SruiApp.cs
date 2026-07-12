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
/// <see cref="SetNow"/> itself, or lets <see cref="Tick"/> package the
/// real-time iteration.
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
    private bool _sharedAudio;
    private uint _audioPeriodFrames;

    /// <summary>Where <see cref="Audio"/> comes from when a host shares
    /// one manager across several apps (MultiAppHost). A manager from
    /// here is host-owned: the app neither ticks nor disposes it.</summary>
    internal Func<SoundManager>? AudioSource { get; set; }

    /// <summary>Whether this app is the one the user is currently in.
    /// A standalone app is always foreground; a host running several
    /// apps (MultiAppHost) maintains the flag on every switch. The
    /// engine ignores it — it exists for app code to branch on, e.g.
    /// routing a notification to an earcon instead of speech while
    /// backgrounded.</summary>
    public bool IsForeground { get; set; } = true;

    /// <summary>Host-installed key reservations, consulted by
    /// <see cref="ReservedReasonFor"/> after the framework's own. A
    /// multi-app host claims its switching combos here; a standalone
    /// app has none.</summary>
    public Func<KeyCombo, string?>? HostReservations { get; set; }

    /// <summary>The spoken reason a combo cannot be bound in this app,
    /// or null when it is bindable: the framework's categorical
    /// reservations (<see cref="KeyCombo.ReservedReason"/>) plus
    /// whatever the host reserved (<see cref="HostReservations"/>).
    /// Bind dialogs consult this, not KeyCombo.ReservedReason directly,
    /// so an app refuses its host's combos too — evaluated live, so a
    /// reconfigured host combo moves the refusal with it.</summary>
    public string? ReservedReasonFor(KeyCombo combo) =>
        combo.ReservedReason ?? HostReservations?.Invoke(combo);

    /// <summary>The audio device period in frames. The getter returns
    /// the period the device actually granted, creating the app-owned
    /// manager on first use exactly like <see cref="Audio"/>. The
    /// setter requests a new period: 0 selects the 128-frame
    /// low-latency default (~2.7ms at 48kHz); heavy scenes (hundreds
    /// of voices, many HRTF positions) buy mixing headroom with e.g.
    /// 512. Setting it while the manager exists reconfigures it in
    /// place (<see cref="SoundManager.Reconfigure"/>): every sound and
    /// group survives with its state — source, position, effects,
    /// transport, playback cursor — replayed against the new device,
    /// but output gaps briefly while the device restarts, so change it
    /// at silence-tolerant moments (startup, menus, an options apply).
    /// Re-assigning the current request is a no-op. Under a multi-app
    /// host there is one shared device: setting this reconfigures it
    /// for every app.</summary>
    public uint AudioPeriodFrames
    {
        get => Audio.DevicePeriodFrames;
        set
        {
            if (value == _audioPeriodFrames)
                return;
            _audioPeriodFrames = value;
            _audio?.Reconfigure(value);
        }
    }

    /// <summary>The audio device sample rate in Hz, creating the
    /// app-owned manager on first use exactly like
    /// <see cref="Audio"/>.</summary>
    public uint AudioSampleRate => Audio.SampleRate;

    /// <summary>Game audio, created on first use and owned by the app.
    /// The event loop advances its automation (pitch tweens,
    /// spatialization) every iteration — <see cref="LoopWaitMs"/> at
    /// idle — so SoundManager.Tick never needs manual driving here.
    /// Consumers without an SruiApp use Srui.Audio standalone and tick
    /// their own loop. Under a multi-app host the manager is the host's
    /// shared one, ticked and disposed by the host.</summary>
    public SoundManager Audio => _audio ??= CreateAudio();

    private SoundManager CreateAudio()
    {
        if (AudioSource is { } source)
        {
            _sharedAudio = true;
            return source();
        }
        return new SoundManager(_audioPeriodFrames);
    }

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
    private bool _quit;

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
    /// readers, push inputs, and drive the clock and the drain yourself
    /// (deterministically via <see cref="SetNow"/>/<see cref="DispatchEvents"/>,
    /// or on real time via <see cref="Tick"/>).</summary>
    public static SruiApp Headless() => new();

    /// <summary>Install a platform clipboard (headless hosts; a windowed
    /// app already carries the system clipboard).</summary>
    public void SetClipboard(IClipboard clipboard) => Engine.SetClipboard(clipboard);

    public void Dispose()
    {
        if (!_sharedAudio)
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
    /// loop (<see cref="LoopWaitMs"/> at idle).</summary>
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

    /// <summary>The engine clock, monotonic milliseconds — the value
    /// the last <see cref="SetNow"/> supplied, which the windowed event
    /// loop refreshes every iteration (<see cref="LoopWaitMs"/> at
    /// idle). Frame-coherent: reads
    /// between two loop iterations all see the same value, the one the
    /// engine's tickers and typeahead saw; game logic usually wants
    /// exactly that. For finer-than-loop timing use
    /// <see cref="PreciseNow"/>.</summary>
    public ulong Now => Engine.Now;

    /// <summary>A live monotonic reading of the clock behind
    /// <see cref="Now"/>, sub-microsecond, advancing between loop
    /// iterations — for timing finer than the event-loop cadence. On a
    /// headless app this returns the engine clock instead, so
    /// SetNow-driven tests stay deterministic.</summary>
    public TimeSpan PreciseNow =>
        Host == null ? TimeSpan.FromMilliseconds(Engine.Now) : _clock.Elapsed;

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

    /// <summary>How long one idle event-loop iteration waits for input,
    /// in milliseconds (default 2). Input interrupts the wait
    /// immediately, so this bounds only idle latency: the cadence at
    /// which tickers fire, audio automation advances, and
    /// <see cref="Now"/> refreshes. Smaller buys finer timing for more
    /// wakeups; 0 busy-polls. Takes effect on the next
    /// iteration.</summary>
    public uint LoopWaitMs { get; set; } = 2;

    /// <summary>Stop the event loop after the current iteration:
    /// <see cref="Run"/> returns, and a host driving <see cref="Tick"/>
    /// itself sees it return false.</summary>
    public void Quit() => _quit = true;

    /// <summary>One event-loop iteration: advance the engine clock from
    /// the app's own stopwatch, advance audio automation, pump and
    /// dispatch window input (blocking up to <see cref="LoopWaitMs"/>;
    /// a headless app has no pump and returns without waiting), then
    /// drain queued events to the readers. Returns false once the
    /// window has closed or <see cref="Quit"/> was called.
    /// <see cref="Run"/> is exactly this in a loop; call Tick yourself
    /// to interleave several apps in one host loop — call
    /// <see cref="EnsureFocus"/> once per app when its tree is built,
    /// and let at most one app own a window (SDL's event queue is
    /// process-global): the rest run headless and take input the host
    /// forwards through <see cref="HandleInput"/>/<see cref="HandleKey"/>.
    /// A tick-driven app runs on real time — don't mix Tick with manual
    /// <see cref="SetNow"/>, which it would overwrite.</summary>
    public bool Tick()
    {
        // Every iteration, not just on input: tickers fire from here,
        // and audio automation advances at the tick cadence. A shared
        // manager is the host's to tick, once for all apps.
        Engine.SetNow((ulong)_clock.ElapsedMilliseconds);
        if (!_sharedAudio)
            _audio?.Tick();
        if (Host is SdlHost host)
        {
            foreach (var hostEvent in host.Pump(LoopWaitMs))
            {
                switch (hostEvent)
                {
                    case HostEvent.Quit:
                        _quit = true;
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
        }
        DispatchEvents();
        return !_quit;
    }

    /// <summary>Run until <see cref="Quit"/> or the window closes.
    /// Requires a windowed app; a headless app is driven by its host —
    /// <see cref="Tick"/> per iteration, or HandleInput/DispatchEvents/
    /// SetNow directly.</summary>
    public void Run()
    {
        if (Host is null)
            throw new InvalidOperationException(
                "a headless app has no event loop; drive it yourself — Tick per iteration, or HandleInput/DispatchEvents/SetNow directly");
        Engine.EnsureFocus();
        _quit = false;
        while (Tick())
        {
        }
    }
}
