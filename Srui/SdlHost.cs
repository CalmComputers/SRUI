using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Srui.Core;

namespace Srui;

/// <summary>Registers key combos with the operating system so they fire
/// wherever keyboard focus is - the launcher shape (Spotlight, PowerToys
/// Run). A registered combo is exclusive across the session: no other
/// program sees it while it is held, so a caller should choose from the
/// ranges the OS leaves free (Win+Alt on Windows; Command+Option on macOS,
/// which is Ctrl+Alt under the Mac keyboard convention, since Control+Option
/// is VoiceOver's own modifier there) and never promote an ordinary
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
/// the physical → logical input translation and the system-wide hotkeys:
/// registered against the window on Windows, and through Carbon on
/// macOS.</summary>
public sealed class SdlHost : IDisposable, ISystemHotkeys
{
    private IntPtr _window;
    private readonly IntPtr _hwnd;
    private readonly InputMapper _mapper = new();

    /// <summary>The keyboard convention of the platform this host runs on:
    /// <see cref="KeyboardConvention.Mac"/> on macOS (the Command key is a
    /// combo's Ctrl), <see cref="KeyboardConvention.Windows"/> elsewhere.
    /// Physical keys are read under it, and the apps on this host speak
    /// and reserve combos under it.</summary>
    public KeyboardConvention Convention { get; } =
        OperatingSystem.IsMacOS() ? KeyboardConvention.Mac : KeyboardConvention.Windows;
    private readonly HashSet<int> _hotkeys = new();

    /// <summary>macOS hotkey registrations by caller id.</summary>
    private readonly Dictionary<int, IntPtr> _carbonHotkeys = new();

    /// <summary>The Carbon handler that turns hotkey presses into
    /// <see cref="FiredHotkeys"/> entries; installed on the first macOS
    /// registration.</summary>
    private IntPtr _carbonHandler;

    /// <summary>The signature this host's Carbon hotkeys carry, so the
    /// handler passes on any it did not register. 'SRUI'.</summary>
    private const uint CarbonSignature = 0x53525549;

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
        _mapper.Convention = Convention;
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
            foreach (var hotKeyRef in _carbonHotkeys.Values)
                Carbon.UnregisterEventHotKey(hotKeyRef);
            _carbonHotkeys.Clear();
            if (_carbonHandler != IntPtr.Zero)
            {
                Carbon.RemoveEventHandler(_carbonHandler);
                _carbonHandler = IntPtr.Zero;
            }
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
                if (InputMapper.PhysicalCombo(ev.Key, ev.Mod, Convention) is (var downKey, var downMods))
                    output.Add(new HostEvent.Key(new KeyInput(
                        downKey, downMods,
                        ev.Repeat != 0 ? KeyPhase.Repeat : KeyPhase.Press)));
                break;
            case Sdl3.EventKeyUp:
                if (InputMapper.PhysicalCombo(ev.Key, ev.Mod, Convention) is (var upKey, var upMods))
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

    // ── System-wide hotkeys (Windows, macOS) ──

    /// <inheritdoc/>
    public string? Register(int id, KeyCombo combo)
    {
        if (OperatingSystem.IsMacOS())
            return RegisterCarbon(id, combo);
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
        if (_carbonHotkeys.Remove(id, out var hotKeyRef))
            Carbon.UnregisterEventHotKey(hotKeyRef);
    }

    private string? RegisterCarbon(int id, KeyCombo combo)
    {
        if (MacKeyCodeOf(combo.Key) is not { } code)
            return "has no system-wide form";
        if (!InstallCarbonHandler())
            return "system-wide hotkeys are not available here";
        Unregister(id);
        // Under the Mac convention a combo's Ctrl is the Command key and
        // its Win the Control key.
        var modifiers = (combo.Ctrl ? Carbon.CmdKey : 0)
            | (combo.Alt ? Carbon.OptionKey : 0)
            | (combo.Shift ? Carbon.ShiftKey : 0)
            | (combo.Win ? Carbon.ControlKey : 0);
        var hotKeyId = new Carbon.EventHotKeyID { Signature = CarbonSignature, Id = unchecked((uint)id) };
        var status = Carbon.RegisterEventHotKey(
            code, modifiers, hotKeyId, Carbon.GetEventDispatcherTarget(), 0, out var hotKeyRef);
        if (status == Carbon.NoErr)
        {
            _carbonHotkeys[id] = hotKeyRef;
            return null;
        }
        return status == Carbon.EventHotKeyExistsErr
            ? "is already taken by another program"
            : "could not be registered system-wide";
    }

    private unsafe bool InstallCarbonHandler()
    {
        if (_carbonHandler != IntPtr.Zero)
            return true;
        var spec = new Carbon.EventTypeSpec
        {
            EventClass = Carbon.EventClassKeyboard,
            EventKind = Carbon.EventHotKeyPressed,
        };
        return Carbon.InstallEventHandler(Carbon.GetEventDispatcherTarget(), &OnCarbonHotkey,
            1, &spec, IntPtr.Zero, out _carbonHandler) == Carbon.NoErr;
    }

    /// <summary>Carbon calls this for each hotkey press while SDL pumps
    /// Cocoa events - the macOS counterpart of
    /// <see cref="OnWindowsMessage"/>. Presses of hotkeys this host did not
    /// register are passed on.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int OnCarbonHotkey(IntPtr handlerCall, IntPtr theEvent, IntPtr userData)
    {
        Carbon.EventHotKeyID hotKeyId;
        if (Carbon.GetEventParameter(theEvent, Carbon.EventParamDirectObject, Carbon.TypeEventHotKeyID,
                IntPtr.Zero, (nuint)sizeof(Carbon.EventHotKeyID), IntPtr.Zero, &hotKeyId) != Carbon.NoErr
            || hotKeyId.Signature != CarbonSignature)
            return Carbon.EventNotHandledErr;
        FiredHotkeys.Add(unchecked((int)hotKeyId.Id));
        return Carbon.NoErr;
    }

    /// <summary>The macOS virtual key code for a key (kVK_* in HIToolbox's
    /// Events.h, positions on the ANSI layout), or null for one with no
    /// system form (the modifiers themselves, the media keys).</summary>
    private static uint? MacKeyCodeOf(Key key)
    {
        if (key.IsChar(out var c))
        {
            return char.ToLowerInvariant(c) switch
            {
                'a' => 0x00, 's' => 0x01, 'd' => 0x02, 'f' => 0x03, 'h' => 0x04, 'g' => 0x05,
                'z' => 0x06, 'x' => 0x07, 'c' => 0x08, 'v' => 0x09, 'b' => 0x0B, 'q' => 0x0C,
                'w' => 0x0D, 'e' => 0x0E, 'r' => 0x0F, 'y' => 0x10, 't' => 0x11, 'o' => 0x1F,
                'u' => 0x20, 'i' => 0x22, 'p' => 0x23, 'l' => 0x25, 'j' => 0x26, 'k' => 0x28,
                'n' => 0x2D, 'm' => 0x2E,
                '1' => 0x12, '2' => 0x13, '3' => 0x14, '4' => 0x15, '6' => 0x16, '5' => 0x17,
                '9' => 0x19, '7' => 0x1A, '8' => 0x1C, '0' => 0x1D,
                '=' => 0x18, '-' => 0x1B, ']' => 0x1E, '[' => 0x21, '\'' => 0x27, ';' => 0x29,
                '\\' => 0x2A, ',' => 0x2B, '/' => 0x2C, '.' => 0x2F, '`' => 0x32,
                _ => null,
            };
        }
        if (key.IsF(out var n))
        {
            return n switch
            {
                1 => 0x7A, 2 => 0x78, 3 => 0x63, 4 => 0x76, 5 => 0x60, 6 => 0x61, 7 => 0x62,
                8 => 0x64, 9 => 0x65, 10 => 0x6D, 11 => 0x67, 12 => 0x6F, 13 => 0x69, 14 => 0x6B,
                15 => 0x71, 16 => 0x6A, 17 => 0x40, 18 => 0x4F, 19 => 0x50, 20 => 0x5A,
                _ => null,
            };
        }
        return key.Code switch
        {
            1 => 0x24, // Enter (kVK_Return)
            2 => 0x35, // Escape
            3 => 0x30, // Tab
            4 => 0x31, // Space
            5 => 0x7E, // Up
            6 => 0x7D, // Down
            7 => 0x7B, // Left
            8 => 0x7C, // Right
            9 => 0x73, // Home
            10 => 0x77, // End
            11 => 0x75, // Delete (kVK_ForwardDelete)
            12 => 0x33, // Backspace (kVK_Delete)
            13 => 0x74, // Page Up
            14 => 0x79, // Page Down
            _ => null,
        };
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
