using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Srui.Core;

namespace Srui;

/// <summary>Registers key combos with the operating system so they fire
/// wherever keyboard focus is - the launcher shape (Spotlight, PowerToys
/// Run). A registered combo is exclusive across the session: no other
/// program sees it while it is held, so a caller should choose from the
/// ranges the OS leaves free (Win+Alt) and never promote an ordinary
/// in-window binding wholesale. The window's host implements it; a
/// headless host has none. A press arrives as
/// <see cref="HostEvent.Hotkey"/> on the app thread.</summary>
public interface ISystemHotkeys
{
    /// <summary>Claim the combo system-wide under the caller's id. Null
    /// on success; otherwise the spoken reason it was refused - another
    /// program holds it, or the key has no system form. An id already
    /// registered is replaced.</summary>
    string? Register(int id, KeyCombo combo);

    /// <summary>Release the id. Unknown ids are ignored.</summary>
    void Unregister(int id);
}

/// <summary>The SDL window and event pump (keyboard focus surface): a
/// hidden-rendering window that exists to receive keyboard focus, plus
/// the physical → logical input translation and, on Windows, the
/// system-wide hotkeys registered against the window.</summary>
public sealed class SdlHost : IDisposable, ISystemHotkeys
{
    private IntPtr _window;
    private readonly IntPtr _hwnd;
    private readonly InputMapper _mapper = new();
    private readonly HashSet<int> _hotkeys = new();

    /// <summary>When the window last gained focus, or null when nothing
    /// is pending. The focus readout is held for
    /// <see cref="RefocusDelayMs"/> so the screen reader's own
    /// announcement of the window finishes first; spoken at once, ours
    /// is cut off by it. A focus lost in the meantime drops it.</summary>
    private long? _refocusAt;

    /// <summary>Whether the window has gained focus before. The first
    /// gain is the window opening, which the app already announces, so
    /// only later ones - the user coming back - get the readout.</summary>
    private bool _focusedBefore;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private const long RefocusDelayMs = 50;

    /// <summary>Hotkey ids the message hook saw since the last pump.
    /// Static because the hook is an unmanaged function pointer with no
    /// instance; one windowed host per process (SDL's event queue is
    /// process-global) keeps that honest. Written and read on the app
    /// thread only - the hook runs inside SDL's pump, which is our
    /// wait.</summary>
    private static readonly List<int> FiredHotkeys = new();

    public SdlHost(string title, uint width = 400, uint height = 300)
    {
        if (!Sdl3.SDL_Init(Sdl3.InitVideo))
            throw new InvalidOperationException($"SDL init failed: {Sdl3.GetError()}");
        _window = Sdl3.SDL_CreateWindow(title, (int)width, (int)height, 0);
        if (_window == IntPtr.Zero)
        {
            Sdl3.SDL_Quit();
            throw new InvalidOperationException($"SDL window creation failed: {Sdl3.GetError()}");
        }
        // Required for TypeChar (text input) events.
        Sdl3.SDL_StartTextInput(_window);
        if (OperatingSystem.IsWindows())
        {
            _hwnd = Sdl3.SDL_GetPointerProperty(
                Sdl3.SDL_GetWindowProperties(_window), Sdl3.PropWindowWin32Hwnd, IntPtr.Zero);
            if (_hwnd != IntPtr.Zero)
                InstallMessageHook();
        }
    }

    public void Dispose()
    {
        if (_window != IntPtr.Zero)
        {
            foreach (var id in _hotkeys)
                User32.UnregisterHotKey(_hwnd, id);
            _hotkeys.Clear();
            if (_hwnd != IntPtr.Zero)
                RemoveMessageHook();
            Sdl3.SDL_DestroyWindow(_window);
            _window = IntPtr.Zero;
            Sdl3.SDL_Quit();
        }
    }

    /// <summary>Install this host's system clipboard on an app's engine.</summary>
    public void ProvideClipboard(SruiApp app) => app.Engine.SetClipboard(new SdlClipboard());

    /// <summary>Bring the window to the front, restoring it if
    /// minimized. Windows grants the foreground to a process that just
    /// handled a registered hotkey, which is the case this serves; from
    /// anywhere else the OS may only flash the taskbar button.</summary>
    public void RaiseWindow()
    {
        Sdl3.SDL_RestoreWindow(_window);
        Sdl3.SDL_RaiseWindow(_window);
    }

    // Shared result for empty batches: the pump runs a few hundred times
    // a second and is almost always empty, so returning a fresh list per
    // call would put a steady drip of garbage under an idle app. The
    // concrete List return type (rather than IReadOnlyList) keeps
    // foreach on the struct enumerator, which is also allocation-free.
    private static readonly List<HostEvent> EmptyBatch = new();

    /// <summary>Block up to the timeout for events, then drain the batch.
    /// Treat the result as read-only: empty batches are shared.</summary>
    public List<HostEvent> Pump(uint timeoutMs)
    {
        // A hotkey press produces no SDL event - the hook records it
        // while SDL pumps messages inside the wait - so a wait that
        // timed out may still have a batch to deliver.
        if (!Sdl3.SDL_WaitEventTimeout(out var ev, (int)timeoutMs))
            return FiredHotkeys.Count == 0 && !RefocusDue()
                ? EmptyBatch
                : DrainDeferred(new List<HostEvent>());

        var result = new List<HostEvent>();
        Dispatch(in ev, result);
        while (Sdl3.SDL_PollEvent(out ev))
            Dispatch(in ev, result);
        // Alt tap resolves only after the whole batch is seen, so a
        // FocusLost in the same batch can cancel it - as does a hotkey
        // the OS consumed, which SDL never saw as a key.
        if (FiredHotkeys.Count > 0)
            _mapper.CancelAltTap();
        if (_mapper.TakeAltTap())
            result.Add(new HostEvent.AltTap());
        return DrainDeferred(result);
    }

    private bool RefocusDue() =>
        _refocusAt is { } at && _clock.ElapsedMilliseconds - at >= RefocusDelayMs;

    private List<HostEvent> DrainDeferred(List<HostEvent> output)
    {
        if (RefocusDue())
        {
            _refocusAt = null;
            output.Add(new HostEvent.Input(InputEvent.Simple(InputKind.SpeakFocus)));
        }
        return DrainHotkeys(output);
    }

    private static List<HostEvent> DrainHotkeys(List<HostEvent> output)
    {
        foreach (var id in FiredHotkeys)
            output.Add(new HostEvent.Hotkey(id));
        FiredHotkeys.Clear();
        return output;
    }

    private void Dispatch(in Sdl3.Event ev, List<HostEvent> output)
    {
        if (ev.Type == Sdl3.EventQuit)
        {
            output.Add(new HostEvent.Quit());
            return;
        }
        if (ev.Type == Sdl3.EventKeyDown)
            output.Add(new HostEvent.KeyDown());

        // The physical key stream, ahead of the logical Input so a game
        // reacts before the input's side effects land.
        switch (ev.Type)
        {
            case Sdl3.EventKeyDown:
                if (InputMapper.PhysicalCombo(ev.Key, ev.Mod) is (var downKey, var downMods))
                    output.Add(new HostEvent.Key(new KeyInput(
                        downKey, downMods,
                        ev.Repeat != 0 ? KeyPhase.Repeat : KeyPhase.Press)));
                break;
            case Sdl3.EventKeyUp:
                if (InputMapper.PhysicalCombo(ev.Key, ev.Mod) is (var upKey, var upMods))
                    output.Add(new HostEvent.Key(new KeyInput(upKey, upMods, KeyPhase.Release)));
                break;
            case Sdl3.EventWindowFocusLost:
                _refocusAt = null;
                output.Add(new HostEvent.FocusLost());
                break;
        }

        if (_mapper.Map(in ev) is InputEvent input)
        {
            if (input.Kind == InputKind.SpeakFocus)
            {
                if (_focusedBefore)
                    _refocusAt = _clock.ElapsedMilliseconds;
                _focusedBefore = true;
            }
            else
                output.Add(new HostEvent.Input(input));
        }
    }

    // ── System-wide hotkeys (Windows) ──

    /// <inheritdoc/>
    public string? Register(int id, KeyCombo combo)
    {
        if (_hwnd == IntPtr.Zero)
            return "system-wide hotkeys are not available here";
        if (VirtualKeyOf(combo.Key) is not { } vk)
            return "has no system-wide form";
        Unregister(id);
        var modifiers = User32.ModNoRepeat
            | (combo.Ctrl ? User32.ModControl : 0)
            | (combo.Alt ? User32.ModAlt : 0)
            | (combo.Shift ? User32.ModShift : 0)
            | (combo.Win ? User32.ModWin : 0);
        if (User32.RegisterHotKey(_hwnd, id, modifiers, vk))
        {
            _hotkeys.Add(id);
            return null;
        }
        return Marshal.GetLastWin32Error() == User32.ErrorHotkeyAlreadyRegistered
            ? "is already taken by another program"
            : "could not be registered system-wide";
    }

    /// <inheritdoc/>
    public void Unregister(int id)
    {
        if (_hotkeys.Remove(id))
            User32.UnregisterHotKey(_hwnd, id);
    }

    /// <summary>The Windows virtual-key code for a key, or null for one
    /// with no system form (the modifiers themselves).</summary>
    private static uint? VirtualKeyOf(Key key)
    {
        if (key.IsChar(out var c))
        {
            if (char.IsAsciiLetter(c))
                return char.ToUpperInvariant(c);
            if (char.IsAsciiDigit(c))
                return c;
            return c switch
            {
                ';' => 0xBA, '=' => 0xBB, ',' => 0xBC, '-' => 0xBD, '.' => 0xBE,
                '/' => 0xBF, '`' => 0xC0, '[' => 0xDB, '\\' => 0xDC, ']' => 0xDD,
                '\'' => 0xDE,
                _ => null,
            };
        }
        if (key.IsF(out var n))
            return 0x70u + n - 1;
        return key.Code switch
        {
            1 => 0x0D, // Enter
            2 => 0x1B, // Escape
            3 => 0x09, // Tab
            4 => 0x20, // Space
            5 => 0x26, // Up
            6 => 0x28, // Down
            7 => 0x25, // Left
            8 => 0x27, // Right
            9 => 0x24, // Home
            10 => 0x23, // End
            11 => 0x2E, // Delete
            12 => 0x08, // Backspace
            13 => 0x21, // Page Up
            14 => 0x22, // Page Down
            15 => 0xB3, // Media Play/Pause
            16 => 0xB0, // Media Next
            17 => 0xB1, // Media Previous
            18 => 0xB2, // Media Stop
            _ => null,
        };
    }

    private static unsafe void InstallMessageHook() =>
        Sdl3.SDL_SetWindowsMessageHook(&OnWindowsMessage, IntPtr.Zero);

    private static unsafe void RemoveMessageHook() =>
        Sdl3.SDL_SetWindowsMessageHook(null, IntPtr.Zero);

    /// <summary>SDL calls this for every Windows message it pumps,
    /// before handling it. A WM_HOTKEY carries the id in wParam: MSG is
    /// hwnd(0) message(8) wParam(16) lParam(24) on x64. Returns true so
    /// SDL still sees everything; a hotkey message means nothing to
    /// it.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe byte OnWindowsMessage(IntPtr userdata, IntPtr msg)
    {
        var message = *(uint*)(msg + 8);
        if (message == User32.WmHotkey)
            FiredHotkeys.Add((int)*(nint*)(msg + 16));
        return 1;
    }
}

/// <summary>The system clipboard via SDL.</summary>
internal sealed class SdlClipboard : IClipboard
{
    public string? Read()
    {
        var ptr = Sdl3.SDL_GetClipboardText();
        if (ptr == IntPtr.Zero)
            return null;
        try
        {
            var text = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(ptr);
            return string.IsNullOrEmpty(text) ? null : text;
        }
        finally
        {
            Sdl3.SDL_free(ptr);
        }
    }

    public void Write(string text) => Sdl3.SDL_SetClipboardText(text);
}
