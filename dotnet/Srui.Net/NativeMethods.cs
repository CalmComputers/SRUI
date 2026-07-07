using System.Runtime.InteropServices;

namespace Srui;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeEvent
{
    public uint Kind;
    public ulong Node;
    public long Num0;
    public IntPtr Speech;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeHostEvent
{
    public uint Kind;
    public uint InputKind;
    public uint Ch;
    public uint Key;
    public uint Mods;
}

internal static partial class NativeMethods
{
    private const string Lib = "srui_ffi";

    // ── Strings ──
    [DllImport(Lib)] internal static extern void srui_string_free(IntPtr s);

    // ── Ui lifecycle ──
    [DllImport(Lib)] internal static extern IntPtr srui_ui_new();
    [DllImport(Lib)] internal static extern void srui_ui_free(IntPtr ui);
    [DllImport(Lib)] internal static extern void srui_ui_set_now(IntPtr ui, ulong nowMs);
    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool srui_ui_ensure_focus(IntPtr ui);
    [DllImport(Lib)] internal static extern void srui_ui_set_focus(IntPtr ui, ulong node);
    [DllImport(Lib)] internal static extern ulong srui_ui_focus(IntPtr ui);
    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool srui_ui_handle_input(IntPtr ui, uint kind, uint ch, uint key, uint mods);
    [DllImport(Lib)] internal static extern void srui_ui_announce(IntPtr ui, [MarshalAs(UnmanagedType.LPUTF8Str)] string text);
    [DllImport(Lib)] internal static extern void srui_ui_reannounce_with_context(IntPtr ui);
    [DllImport(Lib)] internal static extern ulong srui_ui_add_ticker(IntPtr ui, ulong intervalMs);
    [DllImport(Lib)] internal static extern void srui_ui_remove_ticker(IntPtr ui, ulong id);
    [DllImport(Lib)] internal static extern void srui_ui_set_primary(IntPtr ui, ulong node);
    [DllImport(Lib)] internal static extern void srui_ui_set_cancel(IntPtr ui, ulong node);
    [DllImport(Lib)] internal static extern void srui_ui_push_layer(IntPtr ui);
    [DllImport(Lib)] internal static extern void srui_ui_pop_layer(IntPtr ui);
    [DllImport(Lib)] internal static extern void srui_ui_remove(IntPtr ui, ulong node);

    // ── Node constructors ──
    [DllImport(Lib)] internal static extern ulong srui_ui_text_label(IntPtr ui, ulong parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string text);
    [DllImport(Lib)] internal static extern ulong srui_ui_group(IntPtr ui, ulong parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Lib)] internal static extern ulong srui_ui_button(IntPtr ui, ulong parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Lib)] internal static extern ulong srui_ui_checkbox(IntPtr ui, ulong parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.I1)] bool isChecked);
    [DllImport(Lib)] internal static extern ulong srui_ui_editbox(IntPtr ui, ulong parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, [MarshalAs(UnmanagedType.I1)] bool multiline);
    [DllImport(Lib)] internal static extern ulong srui_ui_listbox(IntPtr ui, ulong parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr[] items, nuint itemsLen, [MarshalAs(UnmanagedType.I1)] bool numbered);
    [DllImport(Lib)] internal static extern ulong srui_ui_filter_listbox(IntPtr ui, ulong parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr[] items, nuint itemsLen);
    [DllImport(Lib)] internal static extern ulong srui_ui_slider(IntPtr ui, ulong parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int value, int min, int max, int smallStep, int largeStep, [MarshalAs(UnmanagedType.LPUTF8Str)] string unit);
    [DllImport(Lib)] internal static extern ulong srui_ui_tab_control(IntPtr ui, ulong parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr[] tabs, nuint tabsLen, nuint active);
    [DllImport(Lib)] internal static extern ulong srui_ui_shortcut_field(IntPtr ui, ulong parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    // ── Widget state ──
    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool srui_ui_checkbox_checked(IntPtr ui, ulong node);
    [DllImport(Lib)] internal static extern IntPtr srui_ui_editbox_text(IntPtr ui, ulong node);
    [DllImport(Lib)] internal static extern void srui_ui_set_editbox_text(IntPtr ui, ulong node, [MarshalAs(UnmanagedType.LPUTF8Str)] string text);
    [DllImport(Lib)] internal static extern void srui_ui_set_editbox_read_only(IntPtr ui, ulong node, [MarshalAs(UnmanagedType.I1)] bool readOnly);
    [DllImport(Lib)] internal static extern long srui_ui_listbox_selected(IntPtr ui, ulong node);
    [DllImport(Lib)] internal static extern IntPtr srui_ui_listbox_selected_item(IntPtr ui, ulong node);
    [DllImport(Lib)] internal static extern void srui_ui_set_list_items(IntPtr ui, ulong node, IntPtr[] items, nuint itemsLen);
    [DllImport(Lib)] internal static extern IntPtr srui_ui_filter_selected_item(IntPtr ui, ulong node);
    [DllImport(Lib)] internal static extern int srui_ui_slider_value(IntPtr ui, ulong node);
    [DllImport(Lib)] internal static extern void srui_ui_set_slider_value(IntPtr ui, ulong node, int value);
    [DllImport(Lib)] internal static extern long srui_ui_tab_active(IntPtr ui, ulong node);
    [DllImport(Lib)] internal static extern IntPtr srui_ui_shortcut_combo(IntPtr ui, ulong node);

    // ── Events ──
    [DllImport(Lib)] internal static extern unsafe void srui_ui_drain(IntPtr ui, out NativeEvent* events, out nuint len);
    [DllImport(Lib)] internal static extern unsafe void srui_events_free(NativeEvent* events, nuint len);

    // ── SDL host ──
    [DllImport(Lib)] internal static extern IntPtr srui_host_new([MarshalAs(UnmanagedType.LPUTF8Str)] string title, uint width, uint height);
    [DllImport(Lib)] internal static extern void srui_host_free(IntPtr host);
    [DllImport(Lib)] internal static extern void srui_ui_use_host_clipboard(IntPtr ui, IntPtr host);
    [DllImport(Lib)] internal static extern unsafe void srui_host_pump(IntPtr host, uint timeoutMs, out NativeHostEvent* events, out nuint len);
    [DllImport(Lib)] internal static extern unsafe void srui_host_events_free(NativeHostEvent* events, nuint len);

    // ── Speech ──
    [DllImport(Lib)] internal static extern IntPtr srui_speech_new();
    [DllImport(Lib)] internal static extern void srui_speech_free(IntPtr speech);
    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool srui_speech_speak(IntPtr speech, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, [MarshalAs(UnmanagedType.I1)] bool interrupt);
    [DllImport(Lib)] internal static extern void srui_speech_stop(IntPtr speech);
    [DllImport(Lib)] internal static extern IntPtr srui_speech_backend_name(IntPtr speech);

    // ── Marshaling helpers ──

    internal static string? TakeString(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return null;
        var s = Marshal.PtrToStringUTF8(ptr);
        srui_string_free(ptr);
        return s;
    }

    /// <summary>Allocate a UTF-8 pointer array for a string list; free with FreeUtf8Array.</summary>
    internal static IntPtr[] ToUtf8Array(IReadOnlyList<string> items)
    {
        var ptrs = new IntPtr[items.Count];
        for (var i = 0; i < items.Count; i++)
            ptrs[i] = Marshal.StringToCoTaskMemUTF8(items[i]);
        return ptrs;
    }

    internal static void FreeUtf8Array(IntPtr[] ptrs)
    {
        foreach (var p in ptrs) Marshal.FreeCoTaskMem(p);
    }
}
