using Srui.Audio;

namespace Srui;

/// <summary>
/// One window, several apps: owns the single SDL window, a shared
/// speech reader, and a shared sound manager, and multiplexes them
/// across any number of hosted <see cref="SruiApp"/> instances. Apps
/// are created through <see cref="Add"/> and built normally; exactly
/// one is active at a time — it receives all input and speaks freely,
/// while the others keep running (tickers, messages, state) muted.
/// Switching is user-driven (<see cref="NextAppCombo"/>, default
/// ctrl+tab) or programmatic (<see cref="Activate"/>), and speaks the
/// app's name followed by its focused widget with context, like
/// switching windows. A headless host (<see cref="Headless"/>) has no
/// window and no voice: attach readers and drive
/// <see cref="HandleInput"/>/<see cref="DispatchEvents"/> yourself.
/// </summary>
public sealed class MultiAppHost : IDisposable
{
    /// <summary>The SDL window and event pump; null when headless.</summary>
    public SdlHost? Host { get; }

    private readonly SpeechReader? _speechReader;
    private readonly List<IReader> _readers = new();
    private readonly List<HostedApp> _apps = new();
    private HostedApp? _active;
    private SoundManager? _audio;
    private uint _audioPeriodFrames;
    private bool _quit;

    /// <summary>A windowed host: SDL window and a speech reader over
    /// Prism, shared by every app it will run.</summary>
    public MultiAppHost(string title, uint width = 400, uint height = 300)
    {
        Host = new SdlHost(title, width, height);
        _speechReader = new SpeechReader();
        _readers.Add(_speechReader);
    }

    private MultiAppHost()
    {
    }

    /// <summary>A host with no window and no voice, for tests and
    /// custom embeddings: attach readers, push inputs, and drain
    /// yourself.</summary>
    public static MultiAppHost Headless() => new();

    /// <summary>The Prism speech output behind the shared speech
    /// reader; null when headless.</summary>
    public Speech? Voice => _speechReader?.Voice;

    public void Dispose()
    {
        foreach (var hosted in _apps)
            hosted.App.Dispose();
        _audio?.Dispose();
        _speechReader?.Dispose();
        Host?.Dispose();
    }

    // ── Apps ──

    /// <summary>The hosted apps, in the order they were added.</summary>
    public IReadOnlyList<HostedApp> Apps => _apps;

    /// <summary>The app currently receiving input and speech; null
    /// until the first activation (<see cref="Run"/> activates the
    /// first app if the host program has not chosen one).</summary>
    public HostedApp? Active => _active;

    /// <summary>Create a hosted app: a headless <see cref="SruiApp"/>
    /// pre-wired to the shared window services — system clipboard, the
    /// shared speech reader (muted while the app is in the background),
    /// and the shared sound manager (its <c>Audio</c> resolves to
    /// <see cref="Audio"/>). Build widgets into <c>.App</c> as usual.
    /// Adding does not activate: the app starts in the background.</summary>
    public HostedApp Add(string name)
    {
        var app = SruiApp.Headless();
        app.IsForeground = false;
        app.AudioSource = () => Audio;
        Host?.ProvideClipboard(app);
        var hosted = new HostedApp(this, name, app);
        app.AddReader(new Forwarder(hosted));
        _apps.Add(hosted);
        return hosted;
    }

    /// <summary>Make an app the active one: the previous app is
    /// backgrounded (its <see cref="SruiApp.FocusLost"/> runs, so
    /// held-key state zeroes), and the new one takes over input and
    /// speech, announcing its name and then its focused widget with
    /// context — the window-switch announcement. Activating the active
    /// app just re-announces it.</summary>
    public void Activate(HostedApp hosted)
    {
        if (!ReferenceEquals(hosted.Owner, this))
            throw new ArgumentException("the app belongs to a different host");
        if (!ReferenceEquals(_active, hosted))
        {
            if (_active is { } previous)
            {
                previous.App.IsForeground = false;
                previous.App.FocusLost?.Invoke();
                previous.RaiseDeactivated();
            }
            _active = hosted;
            hosted.App.IsForeground = true;
        }
        hosted.App.EnsureFocus();
        hosted.App.Announce(hosted.Name);
        hosted.App.ReannounceWithContext();
        hosted.RaiseActivated();
    }

    // ── Readers ──

    /// <summary>Attach a reader to the shared stream: it hears the
    /// active app in full and, from background apps, only the
    /// announcements of those with
    /// <see cref="HostedApp.AnnouncesInBackground"/> set. Per-app
    /// readers (attached to a hosted <see cref="SruiApp"/> directly)
    /// bypass this policy and hear their app unfiltered.</summary>
    public void AddReader(IReader reader) => _readers.Add(reader);

    /// <summary>Detach a shared reader. True when it was attached.</summary>
    public bool RemoveReader(IReader reader) => _readers.Remove(reader);

    /// <summary>Tell the shared readers the user acted (there is one
    /// voice, so one silence). The windowed loop calls this on every
    /// key-down.</summary>
    public void Interrupt()
    {
        foreach (var reader in _readers)
            reader.OnInterrupt();
        _active?.App.Interrupt();
    }

    // ── Audio ──

    /// <summary>The shared sound manager, created on first use — by
    /// the host or by any hosted app touching its <c>Audio</c>. One
    /// device for the whole program, ticked once per loop
    /// iteration.</summary>
    public SoundManager Audio => _audio ??= new SoundManager(_audioPeriodFrames);

    /// <summary>The shared audio device period in frames, as
    /// <see cref="SruiApp.AudioPeriodFrames"/>: the getter reads the
    /// granted period, the setter reconfigures the device in place —
    /// for every app at once.</summary>
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

    // ── Host hooks ──

    /// <summary>Input nothing consumed — not the active app, nor its
    /// own UnhandledInput. Return true when handled.</summary>
    public Func<InputEvent, bool>? UnhandledInput { get; set; }

    /// <summary>Physical key transitions the active app declined.
    /// Return true when handled.</summary>
    public Func<KeyInput, bool>? UnhandledKey { get; set; }

    /// <summary>The window lost keyboard focus. The active app's own
    /// <see cref="SruiApp.FocusLost"/> has already run.</summary>
    public Action? FocusLost { get; set; }

    /// <summary>A clean Alt tap. When unset, the active app's own
    /// <see cref="SruiApp.AltTap"/> runs instead.</summary>
    public Action? AltTap { get; set; }

    // ── Switching combos ──

    /// <summary>Cycle to the next app (default ctrl+tab). Checked
    /// before the active app sees the input, so no app can trap the
    /// user; effectively reserved while hosted — a hosted bind dialog
    /// should refuse it. Null disables.</summary>
    public KeyCombo? NextAppCombo { get; set; } = KeyCombo.WithCtrl(Key.Tab);

    /// <summary>Cycle to the previous app (default ctrl+shift+tab).
    /// Null disables.</summary>
    public KeyCombo? PreviousAppCombo { get; set; } = KeyCombo.CtrlShift(Key.Tab);

    private void Cycle(int direction)
    {
        if (_apps.Count == 0)
            return;
        var index = _active is { } active ? _apps.IndexOf(active) : 0;
        index = (index + direction + _apps.Count) % _apps.Count;
        Activate(_apps[index]);
    }

    // ── Driving (Run does this; headless hosts do it themselves) ──

    /// <summary>Dispatch one logical input: the switching combos
    /// first, then the active app's whole claim order, then
    /// <see cref="UnhandledInput"/>. False when nothing consumed it.</summary>
    public bool HandleInput(in InputEvent input)
    {
        if (NextAppCombo is { } next && input.Is(next))
        {
            Cycle(1);
            return true;
        }
        if (PreviousAppCombo is { } previous && input.Is(previous))
        {
            Cycle(-1);
            return true;
        }
        if (_active?.App.HandleInput(input) == true)
            return true;
        return UnhandledInput?.Invoke(input) == true;
    }

    /// <summary>Dispatch one physical key transition to the active
    /// app, then <see cref="UnhandledKey"/>. False when nothing
    /// claimed it.</summary>
    public bool HandleKey(in KeyInput key)
    {
        if (_active?.App.HandleKey(key) == true)
            return true;
        return UnhandledKey?.Invoke(key) == true;
    }

    /// <summary>Deliver queued messages to their apps, then drain every
    /// app's events to its readers — the deterministic drain for
    /// headless hosts; <see cref="Tick"/> does this every iteration.</summary>
    public void DispatchEvents()
    {
        foreach (var hosted in _apps)
            hosted.DeliverMessages();
        foreach (var hosted in _apps)
            hosted.App.DispatchEvents();
    }

    // ── The loop ──

    /// <summary>How long one idle iteration waits for input, in
    /// milliseconds (default 2), as <see cref="SruiApp.LoopWaitMs"/>.</summary>
    public uint LoopWaitMs { get; set; } = 2;

    /// <summary>Stop the loop after the current iteration.</summary>
    public void Quit() => _quit = true;

    /// <summary>One loop iteration: pump and route window input,
    /// advance the shared audio, deliver queued messages, then tick
    /// every hosted app (clock, tickers, drain). Returns false once
    /// the window has closed or <see cref="Quit"/> was called. A
    /// hosted app's own Quit does not stop the host.</summary>
    public bool Tick()
    {
        if (Host is { } host)
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
                        if (AltTap is { } altTap)
                            altTap();
                        else
                            _active?.App.AltTap?.Invoke();
                        break;
                    case HostEvent.Key(var keyInput):
                        HandleKey(keyInput);
                        break;
                    case HostEvent.FocusLost:
                        _active?.App.FocusLost?.Invoke();
                        FocusLost?.Invoke();
                        break;
                    case HostEvent.Input(var input):
                        HandleInput(input);
                        break;
                }
            }
        }
        _audio?.Tick();
        foreach (var hosted in _apps)
            hosted.DeliverMessages();
        foreach (var hosted in _apps)
            hosted.App.Tick();
        return !_quit;
    }

    /// <summary>Run until <see cref="Quit"/> or the window closes,
    /// activating the first app if none is active yet. Requires a
    /// windowed host.</summary>
    public void Run()
    {
        if (Host is null)
            throw new InvalidOperationException(
                "a headless host has no event loop; drive it yourself — Tick per iteration, or HandleInput/DispatchEvents directly");
        if (_active is null && _apps.Count > 0)
            Activate(_apps[0]);
        _quit = false;
        while (Tick())
        {
        }
    }

    /// <summary>The shared-reader policy: the active app passes
    /// everything; a background app passes only announcements, and
    /// only when it opted in.</summary>
    private sealed class Forwarder : IReader
    {
        private readonly HostedApp _hosted;

        public Forwarder(HostedApp hosted) => _hosted = hosted;

        public void OnEvent(AccessibilityEvent e)
        {
            if (!_hosted.IsActive
                && !(_hosted.AnnouncesInBackground && e is AccessibilityEvent.Announce))
                return;
            foreach (var reader in _hosted.Owner._readers)
                reader.OnEvent(e);
        }

        // Interrupts flow host → apps (the host broadcasts to the
        // shared readers itself), never apps → host.
        public void OnInterrupt()
        {
        }
    }
}

/// <summary>One app under a <see cref="MultiAppHost"/>: the app itself,
/// its spoken name, its background-speech policy, and a mailbox for
/// messages from the rest of the program.</summary>
public sealed class HostedApp
{
    internal MultiAppHost Owner { get; }

    /// <summary>The name switching announces.</summary>
    public string Name { get; }

    /// <summary>The app: build widgets into it as usual.</summary>
    public SruiApp App { get; }

    private List<object> _mailbox = new();

    internal HostedApp(MultiAppHost owner, string name, SruiApp app)
    {
        Owner = owner;
        Name = name;
        App = app;
    }

    /// <summary>Whether this is the app the user is in.</summary>
    public bool IsActive => ReferenceEquals(Owner.Active, this);

    /// <summary>Let this app's announcements (<see cref="SruiApp.Announce"/>
    /// and widget-emitted announce events — nothing else) through the
    /// shared reader while backgrounded. Off by default: a background
    /// app is silent, and asynchronous urgency belongs to earcons.</summary>
    public bool AnnouncesInBackground { get; set; }

    /// <summary>The app became the active one (already announced).</summary>
    public event Action? Activated;

    /// <summary>The app was backgrounded; its FocusLost has run.</summary>
    public event Action? Deactivated;

    /// <summary>A message from <see cref="Send"/>, delivered at drain
    /// time — outside input dispatch, same thread — whether or not the
    /// app is active. Pattern-match on the message type.</summary>
    public event Action<object>? MessageReceived;

    /// <summary>Queue a message for this app; any part of the program
    /// (typically another app's handler) may call it. Delivered on the
    /// host's next tick or <see cref="MultiAppHost.DispatchEvents"/>;
    /// a message sent from a delivery handler waits for the next one.</summary>
    public void Send(object message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _mailbox.Add(message);
    }

    internal void RaiseActivated() => Activated?.Invoke();

    internal void RaiseDeactivated() => Deactivated?.Invoke();

    internal void DeliverMessages()
    {
        if (_mailbox.Count == 0)
            return;
        // Swap before delivering: a handler that sends to its own app
        // queues for the next drain instead of looping this one.
        var batch = _mailbox;
        _mailbox = new List<object>();
        foreach (var message in batch)
            MessageReceived?.Invoke(message);
    }
}
