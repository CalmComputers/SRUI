using System.Runtime.InteropServices;

namespace Srui.Core;

/// <summary>Hand-written P/Invoke over the slice of SDL3 the host layer
/// uses: init, one window, the event queue, and the clipboard. Constants
/// and struct offsets verified against SDL 3.4.4 headers. SDL's bool is
/// one byte, hence the I1 marshaling on every bool return.</summary>
internal static class Sdl3
{
    private const string Dll = "SDL3";

    public const uint InitVideo = 0x20;

    // SDL_EventType values.
    public const uint EventQuit = 0x100;
    public const uint EventWindowFocusGained = 526;
    public const uint EventWindowFocusLost = 527;
    public const uint EventKeyDown = 0x300;
    public const uint EventKeyUp = 0x301;
    public const uint EventTextInput = 0x303;

    // SDL_Keymod bits (Uint16).
    public const ushort KmodLShift = 0x0001;
    public const ushort KmodRShift = 0x0002;
    public const ushort KmodLCtrl = 0x0040;
    public const ushort KmodRCtrl = 0x0080;
    public const ushort KmodLAlt = 0x0100;
    public const ushort KmodRAlt = 0x0200;

    public const ushort KmodShift = KmodLShift | KmodRShift;
    public const ushort KmodCtrl = KmodLCtrl | KmodRCtrl;
    public const ushort KmodAlt = KmodLAlt | KmodRAlt;

    // SDL_Keycode values the mapper names. Printable keys are their
    // ASCII codepoint; the rest are scancode | 0x40000000.
    public const uint KeyReturn = 0x0D;
    public const uint KeyEscape = 0x1B;
    public const uint KeyBackspace = 0x08;
    public const uint KeyTab = 0x09;
    public const uint KeySpace = 0x20;
    public const uint KeyDelete = 0x7F;
    public const uint KeyF1 = 0x4000003A; // ..F12 contiguous through 0x40000045
    public const uint KeyF12 = 0x40000045;
    public const uint KeyHome = 0x4000004A;
    public const uint KeyPageUp = 0x4000004B;
    public const uint KeyEnd = 0x4000004D;
    public const uint KeyPageDown = 0x4000004E;
    public const uint KeyRight = 0x4000004F;
    public const uint KeyLeft = 0x40000050;
    public const uint KeyDown = 0x40000051;
    public const uint KeyUp = 0x40000052;
    public const uint KeyKpEnter = 0x40000058;
    public const uint KeyLCtrl = 0x400000E0;
    public const uint KeyLShift = 0x400000E1;
    public const uint KeyLAlt = 0x400000E2;
    public const uint KeyRCtrl = 0x400000E4;
    public const uint KeyRShift = 0x400000E5;
    public const uint KeyRAlt = 0x400000E6;
    public const uint KeyMediaPlay = 0x40000106;
    public const uint KeyMediaPause = 0x40000107;
    public const uint KeyMediaNextTrack = 0x4000010B;
    public const uint KeyMediaPreviousTrack = 0x4000010C;
    public const uint KeyMediaStop = 0x4000010D;
    public const uint KeyMediaPlayPause = 0x4000010F;

    /// <summary>SDL_Event — a 128-byte union. Only the fields the host
    /// reads are declared; keyboard and text-input members overlap by
    /// design. Keyboard layout: type(0) reserved(4) timestamp(8)
    /// windowID(16) which(20) scancode(24) key(28) mod(32,u16) raw(34)
    /// down(36) repeat(37). Text input: text pointer at 24.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    public struct Event
    {
        [FieldOffset(0)] public uint Type;
        // SDL_KeyboardEvent
        [FieldOffset(28)] public uint Key;
        [FieldOffset(32)] public ushort Mod;
        [FieldOffset(36)] public byte Down;
        [FieldOffset(37)] public byte Repeat;
        // SDL_TextInputEvent
        [FieldOffset(24)] public IntPtr TextPtr;
    }

    [DllImport(Dll)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_Init(uint flags);

    [DllImport(Dll)]
    public static extern void SDL_Quit();

    [DllImport(Dll)]
    public static extern IntPtr SDL_CreateWindow(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string title, int w, int h, ulong flags);

    [DllImport(Dll)]
    public static extern void SDL_DestroyWindow(IntPtr window);

    [DllImport(Dll)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_StartTextInput(IntPtr window);

    [DllImport(Dll)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_WaitEventTimeout(out Event ev, int timeoutMs);

    [DllImport(Dll)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_PollEvent(out Event ev);

    [DllImport(Dll)]
    public static extern IntPtr SDL_GetClipboardText();

    [DllImport(Dll)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SDL_SetClipboardText([MarshalAs(UnmanagedType.LPUTF8Str)] string text);

    [DllImport(Dll)]
    public static extern void SDL_free(IntPtr mem);

    [DllImport(Dll)]
    public static extern IntPtr SDL_GetError();

    public static string GetError()
    {
        var ptr = SDL_GetError();
        return ptr == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(ptr) ?? "";
    }
}
