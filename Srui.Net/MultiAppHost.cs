using Srui.Audio;

namespace Srui;

/// <summary>How much an app switch says. The grades map to how the
/// user got there: a cycle combo needs the destination named, a pick
/// from a task list already named it, and a programmatic restore may
/// want to say something else entirely.</summary>
public enum SwitchAnnouncement
{
    /// <summary>The app's name, then its focused widget with context —
    /// the window-switch announcement. What the switching combos use.</summary>
    NameAndFocus,

    /// <summary>Only the focused widget with context. For activations
    /// where the user just chose the app by name — a task list, a
    /// launcher — and repeating it would be noise.</summary>
    FocusOnly,

    /// <summary>Nothing is queued; the caller speaks, or nothing does.
    /// The first time an app ever gains focus, establishing that focus
    /// still announces the focused widget — truly silent switches are
    /// to apps already visited.</summary>
    Silent,
}

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

    /// <summary>The set of hosted apps changed: one was added, or one
    /// closed. A task-list UI refreshes from <see cref="Apps"/> here.</summary>
    public event Action? AppsChanged;

    /// <summary>Create a hosted app: a headless <see cref="SruiApp"/>
    /// pre-wired to the shared window services — system clipboard, the
    /// shared speech reader (muted while the app is in the background),
    /// the shared sound manager (its <c>Audio</c> resolves to
    /// <see cref="Audio"/>), and the host's key reservations. Build
    /// widgets into <c>.App</c> as usual; apps may be added at any
    /// point, including from a running app's handlers. Adding does not
    /// activate: the app starts in the background.</summary>
    public HostedApp Add(string name)
    {
        var app = SruiApp.Headless();
        app.IsForeground = false;
        app.AudioSource = () => Audio;
        app.HostReservations = SwitchReservation;
        Host?.ProvideClipboard(app);
        var hosted = new HostedApp(this, name, app);
        app.AddReader(new Forwarder(hosted));
        _apps.Add(hosted);
        AppsChanged?.Invoke();
        return hosted;
    }

    internal void CloseApp(HostedApp hosted)
    {
        var index = _apps.IndexOf(hosted);
        if (index < 0)
            return;
        _apps.RemoveAt(index);
        if (ReferenceEquals(_active, hosted))
        {
            _active = null;
            hosted.App.IsForeground = false;
            // Land on the neighbor that moved into the closed app's
            // slot, or the new last app; announced like any switch.
            if (_apps.Count > 0)
                Activate(_apps[Math.Min(index, _apps.Count - 1)]);
        }
        hosted.App.Dispose();
        hosted.RaiseClosed();
        AppsChanged?.Invoke();
    }

    /// <summary>Make an app the active one: the previous app is
    /// backgrounded (its <see cref="SruiApp.FocusLost"/> runs, so
    /// held-key state zeroes), and the new one takes over input and
    /// speech. What the switch says is the caller's choice
    /// (<see cref="SwitchAnnouncement"/>): the full name-then-focus
    /// announcement by default, focus alone for a task-list pick,
    /// or nothing. Activating the active app just re-announces it
    /// at the same grade.</summary>
    public void Activate(
        HostedApp hosted, SwitchAnnouncement announcement = SwitchAnnouncement.NameAndFocus)
    {
        if (!ReferenceEquals(hosted.Owner, this))
            throw new ArgumentException("the app belongs to a different host");
        if (hosted.IsClosed)
            throw new InvalidOperationException("the app is closed");
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
        if (announcement == SwitchAnnouncement.NameAndFocus)
            hosted.App.Announce(hosted.Name);
        if (announcement != SwitchAnnouncement.Silent)
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
    /// user. Reserved in every hosted app: their
    /// <see cref="SruiApp.ReservedReasonFor"/> refuses the current
    /// value with a spoken reason. Null disables.</summary>
    public KeyCombo? NextAppCombo { get; set; } = KeyCombo.WithCtrl(Key.Tab);

    /// <summary>Cycle to the previous app (default ctrl+shift+tab).
    /// Null disables.</summary>
    public KeyCombo? PreviousAppCombo { get; set; } = KeyCombo.CtrlShift(Key.Tab);

    private string? SwitchReservation(KeyCombo combo)
    {
        if (combo == NextAppCombo || combo == PreviousAppCombo)
            return $"{combo.DisplayName()} is reserved for switching apps";
        return null;
    }

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
        // By index, not foreach: a handler may Add or Close an app
        // mid-drain. A shifted slot at worst skips one app until the
        // next drain.
        for (var i = 0; i < _apps.Count; i++)
            _apps[i].DeliverMessages();
        for (var i = 0; i < _apps.Count; i++)
            _apps[i].App.DispatchEvents();
    }

    // ── The loop ──

    /// <summary>How long one idle iteration waits for input, in
    /// milliseconds (default 2), as <see cref="SruiApp.LoopWaitMs"/>.</summary>
    public uint LoopWaitMs { get; set; } = 2;

    /// <summary>Stop the loop after the current iteration.</summary>
    public void Quit() => _quit = true;

    /// <summary>Consulted before honoring a window-close (or any
    /// <see cref="RequestQuit"/>). Return false to keep running — the
    /// handler owns whatever the user must resolve first (unsaved
    /// changes, a confirmation). Null means quit unconditionally.</summary>
    public Func<bool>? QuitRequested { get; set; }

    /// <summary>The guarded quit path: asks <see cref="QuitRequested"/>
    /// and stops the loop only on consent, returning whether the quit
    /// is going ahead. The window's close button routes here.</summary>
    public bool RequestQuit()
    {
        if (QuitRequested is { } requested && !requested())
            return false;
        _quit = true;
        return true;
    }

    /// <summary>One loop iteration: pump and route window input,
    /// advance the shared audio, deliver queued messages, then tick
    /// every hosted app (clock, tickers, drain). Returns false once
    /// the window has closed or <see cref="Quit"/> was called. A
    /// hosted app's own Quit closes that app
    /// (<see cref="HostedApp.Close"/>) instead of stopping the host —
    /// an app's exit path works the same standalone and hosted.</summary>
    public bool Tick()
    {
        if (Host is { } host)
        {
            foreach (var hostEvent in host.Pump(LoopWaitMs))
            {
                switch (hostEvent)
                {
                    case HostEvent.Quit:
                        RequestQuit();
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
        // By index, not foreach: handlers may Add or Close apps
        // mid-iteration (see DispatchEvents).
        for (var i = 0; i < _apps.Count; i++)
            _apps[i].DeliverMessages();
        for (var i = 0; i < _apps.Count; i++)
        {
            var hosted = _apps[i];
            if (!hosted.App.Tick())
                hosted.Close();
        }
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

    /// <summary>The name switching announces. Read at announcement
    /// time, so a rename (an editor adopting its document's name, a
    /// player showing its track) speaks from the next switch on; the
    /// change itself is silent. Task lists reading it live via
    /// <see cref="IListItem.Text"/> need no sync call either.</summary>
    public string Name { get; set; }

    /// <summary>The app: build widgets into it as usual.</summary>
    public SruiApp App { get; }

    private List<object> _mailbox = new();
    private bool _closed;

    internal HostedApp(MultiAppHost owner, string name, SruiApp app)
    {
        Owner = owner;
        Name = name;
        App = app;
    }

    /// <summary>Whether this is the app the user is in.</summary>
    public bool IsActive => ReferenceEquals(Owner.Active, this);

    /// <summary>Whether <see cref="Close"/> has run (directly, or via
    /// the app's own Quit).</summary>
    public bool IsClosed => _closed;

    /// <summary>Close the app: it leaves the host's list (switching
    /// skips it, messages to it drop), its <see cref="SruiApp"/> is
    /// disposed, and — when it was the active app — its neighbor is
    /// activated and announced. Closing a background app is silent.
    /// Idempotent. Like any operation reaching beyond a widget's own
    /// state, call it at drain time (an Activated handler, or Post);
    /// an app closing itself may equivalently call its own Quit, which
    /// the host turns into Close on the next tick.</summary>
    public void Close()
    {
        if (_closed)
            return;
        _closed = true;
        Owner.CloseApp(this);
    }

    /// <summary>The app closed and left the host. A task-list UI can
    /// also watch <see cref="MultiAppHost.AppsChanged"/>.</summary>
    public event Action? Closed;

    internal void RaiseClosed() => Closed?.Invoke();

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
    /// a message sent from a delivery handler waits for the next one.
    /// A message to a closed app is dropped silently, so senders need
    /// no lifetime bookkeeping.</summary>
    public void Send(object message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (_closed)
            return;
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
