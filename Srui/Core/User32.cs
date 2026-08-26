using System.Runtime.InteropServices;

namespace Srui.Core;

/// <summary>Hand-written P/Invoke over the slice of user32 the host
/// layer uses: system-wide hotkeys. Windows-only; the host checks the
/// platform before calling. Constants from winuser.h.</summary>
internal static class User32
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    /// <summary>Holding the combo fires once, not on every auto-repeat.</summary>
    public const uint ModNoRepeat = 0x4000;

    public const uint WmHotkey = 0x0312;

    /// <summary>GetLastError after a refused RegisterHotKey when another
    /// window in the session already holds the combo.</summary>
    public const int ErrorHotkeyAlreadyRegistered = 1409;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
