using System.Runtime.InteropServices;

namespace Srui.Core;

/// <summary>Hand-written P/Invoke over the slice of Carbon's HIToolbox
/// the host layer uses: system-wide hotkeys, the macOS counterpart of
/// <see cref="User32"/>. Carbon hotkeys need no accessibility permission,
/// and their presses reach the event dispatcher target while SDL pumps
/// Cocoa events. macOS-only; the host checks the platform before calling.
/// Signatures and constants from HIToolbox's CarbonEvents.h,
/// CarbonEventsCore.h, and Events.h.</summary>
internal static class Carbon
{
    private const string Lib = "/System/Library/Frameworks/Carbon.framework/Carbon";

    // Modifier masks for RegisterEventHotKey (Events.h).
    public const uint CmdKey = 1 << 8;
    public const uint ShiftKey = 1 << 9;
    public const uint OptionKey = 1 << 11;
    public const uint ControlKey = 1 << 12;

    public const uint EventClassKeyboard = 0x6B657962; // 'keyb'
    public const uint EventHotKeyPressed = 5;
    public const uint EventParamDirectObject = 0x2D2D2D2D; // '----'
    public const uint TypeEventHotKeyID = 0x686B6964; // 'hkid'

    public const int NoErr = 0;
    /// <summary>RegisterEventHotKey's refusal when the combo is already
    /// registered, by this or another program.</summary>
    public const int EventHotKeyExistsErr = -9878;
    /// <summary>A handler's answer for an event it leaves to the next
    /// handler.</summary>
    public const int EventNotHandledErr = -9874;

    [StructLayout(LayoutKind.Sequential)]
    public struct EventHotKeyID
    {
        public uint Signature;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventTypeSpec
    {
        public uint EventClass;
        public uint EventKind;
    }

    [DllImport(Lib)]
    public static extern IntPtr GetEventDispatcherTarget();

    [DllImport(Lib)]
    public static extern int RegisterEventHotKey(
        uint hotKeyCode, uint hotKeyModifiers, EventHotKeyID hotKeyId,
        IntPtr target, uint options, out IntPtr hotKeyRef);

    [DllImport(Lib)]
    public static extern int UnregisterEventHotKey(IntPtr hotKeyRef);

    [DllImport(Lib)]
    public static extern unsafe int InstallEventHandler(
        IntPtr target, delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int> handler,
        nuint numTypes, EventTypeSpec* list, IntPtr userData, out IntPtr handlerRef);

    [DllImport(Lib)]
    public static extern int RemoveEventHandler(IntPtr handlerRef);

    [DllImport(Lib)]
    public static extern unsafe int GetEventParameter(
        IntPtr theEvent, uint name, uint desiredType, IntPtr actualType,
        nuint bufferSize, IntPtr actualSize, void* data);
}
