namespace Srui;

/// <summary>
/// The retained accessibility tree and input dispatcher. Build widgets,
/// feed input, drain output events. Not thread-safe: one Ui, one thread.
/// Subclass the widget wrapper methods' results as you see fit — node
/// handles are plain values, so C#-side widget classes compose freely.
/// </summary>
public sealed class Ui : IDisposable
{
    internal IntPtr Handle;

    public Ui()
    {
        Handle = NativeMethods.srui_ui_new();
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            NativeMethods.srui_ui_free(Handle);
            Handle = IntPtr.Zero;
        }
    }

    // ── Clock, focus, input ──

    /// <summary>Advance the monotonic clock (drives typeahead timeouts).</summary>
    public void SetNow(ulong milliseconds) => NativeMethods.srui_ui_set_now(Handle, milliseconds);

    /// <summary>Focus the first focusable node if nothing is focused.</summary>
    public bool EnsureFocus() => NativeMethods.srui_ui_ensure_focus(Handle);

    public void SetFocus(NodeId node) => NativeMethods.srui_ui_set_focus(Handle, node.Value);

    public NodeId Focus => new(NativeMethods.srui_ui_focus(Handle));

    /// <summary>Dispatch one input. Returns false when unconsumed — the
    /// host's own bindings get it then.</summary>
    public bool HandleInput(in InputEvent input) =>
        NativeMethods.srui_ui_handle_input(Handle, (uint)input.Kind, input.Ch, input.Key, (uint)input.Mods);

    /// <summary>Queue a free-form announcement for the readers.</summary>
    public void Announce(string text) => NativeMethods.srui_ui_announce(Handle, text);

    /// <summary>Re-announce the focused node with its context labels
    /// (preceding Label siblings) — the dialog-open announcement.</summary>
    public void ReannounceWithContext() => NativeMethods.srui_ui_reannounce_with_context(Handle);

    /// <summary>Register a periodic ticker; Tick events fire at SetNow
    /// resolution. Returns the id carried by the events.</summary>
    public ulong AddTicker(ulong intervalMs) => NativeMethods.srui_ui_add_ticker(Handle, intervalMs);

    public void RemoveTicker(ulong id) => NativeMethods.srui_ui_remove_ticker(Handle, id);

    // ── Layers and defaults ──

    public void SetPrimary(NodeId node) => NativeMethods.srui_ui_set_primary(Handle, node.Value);
    public void SetCancel(NodeId node) => NativeMethods.srui_ui_set_cancel(Handle, node.Value);
    public void PushLayer() => NativeMethods.srui_ui_push_layer(Handle);
    public void PopLayer() => NativeMethods.srui_ui_pop_layer(Handle);
    public void Remove(NodeId node) => NativeMethods.srui_ui_remove(Handle, node.Value);

    /// <summary>Hide/show a node and its subtree. Focus recovers (with
    /// an announcement) if it was inside.</summary>
    public void SetHidden(NodeId node, bool hidden) =>
        NativeMethods.srui_ui_set_hidden(Handle, node.Value, hidden);

    /// <summary>Enable/disable a node. Focus recovers if it was here.</summary>
    public void SetDisabled(NodeId node, bool disabled) =>
        NativeMethods.srui_ui_set_disabled(Handle, node.Value, disabled);

    /// <summary>Rename a node; re-announces when focused.</summary>
    public void SetNodeName(NodeId node, string name) =>
        NativeMethods.srui_ui_set_node_name(Handle, node.Value, name);

    /// <summary>Change a node's spoken description.</summary>
    public void SetNodeDescription(NodeId node, string description) =>
        NativeMethods.srui_ui_set_node_description(Handle, node.Value, description);

    /// <summary>Attach a shortcut to a node. <paramref name="combo"/> is
    /// the config form ("ctrl+shift+s"). Returns false when the combo
    /// fails to parse.</summary>
    public bool AddShortcut(NodeId node, string combo, ShortcutAction action) =>
        NativeMethods.srui_ui_add_shortcut(Handle, node.Value, combo, (uint)action);

    /// <summary>Remove every shortcut from a node.</summary>
    public void ClearShortcuts(NodeId node) =>
        NativeMethods.srui_ui_clear_shortcuts(Handle, node.Value);

    // ── Widgets ──

    public NodeId TextLabel(NodeId parent, string text) =>
        new(NativeMethods.srui_ui_text_label(Handle, parent.Value, text));

    public NodeId Group(NodeId parent, string name) =>
        new(NativeMethods.srui_ui_group(Handle, parent.Value, name));

    /// <summary>A custom widget: focusable, no spoken role, no built-in
    /// behavior — every key falls through to the host's bindings.</summary>
    public NodeId Custom(NodeId parent, string name) =>
        new(NativeMethods.srui_ui_custom(Handle, parent.Value, name));

    public NodeId Button(NodeId parent, string name) =>
        new(NativeMethods.srui_ui_button(Handle, parent.Value, name));

    public NodeId Checkbox(NodeId parent, string name, bool isChecked) =>
        new(NativeMethods.srui_ui_checkbox(Handle, parent.Value, name, isChecked));

    public NodeId Editbox(NodeId parent, string name, string text = "", bool multiline = false) =>
        new(NativeMethods.srui_ui_editbox(Handle, parent.Value, name, text, multiline));

    public NodeId Listbox(NodeId parent, string name, IReadOnlyList<string> items, bool numbered = false)
    {
        var ptrs = NativeMethods.ToUtf8Array(items);
        try
        {
            return new(NativeMethods.srui_ui_listbox(Handle, parent.Value, name, ptrs, (nuint)items.Count, numbered));
        }
        finally
        {
            NativeMethods.FreeUtf8Array(ptrs);
        }
    }

    public NodeId FilterListbox(NodeId parent, string name, IReadOnlyList<string> items)
    {
        var ptrs = NativeMethods.ToUtf8Array(items);
        try
        {
            return new(NativeMethods.srui_ui_filter_listbox(Handle, parent.Value, name, ptrs, (nuint)items.Count));
        }
        finally
        {
            NativeMethods.FreeUtf8Array(ptrs);
        }
    }

    public NodeId Slider(
        NodeId parent, string name, int value, int min, int max,
        int smallStep = 1, int largeStep = 10, string unit = "") =>
        new(NativeMethods.srui_ui_slider(Handle, parent.Value, name, value, min, max, smallStep, largeStep, unit));

    public NodeId TabControl(NodeId parent, string name, IReadOnlyList<string> tabs, int active = 0)
    {
        var ptrs = NativeMethods.ToUtf8Array(tabs);
        try
        {
            return new(NativeMethods.srui_ui_tab_control(Handle, parent.Value, name, ptrs, (nuint)tabs.Count, (nuint)active));
        }
        finally
        {
            NativeMethods.FreeUtf8Array(ptrs);
        }
    }

    public NodeId ShortcutField(NodeId parent, string name) =>
        new(NativeMethods.srui_ui_shortcut_field(Handle, parent.Value, name));

    // ── Widget state ──

    public bool CheckboxChecked(NodeId node) => NativeMethods.srui_ui_checkbox_checked(Handle, node.Value);

    public string EditboxText(NodeId node) =>
        NativeMethods.TakeString(NativeMethods.srui_ui_editbox_text(Handle, node.Value)) ?? "";

    public void SetEditboxText(NodeId node, string text) =>
        NativeMethods.srui_ui_set_editbox_text(Handle, node.Value, text);

    public void SetEditboxReadOnly(NodeId node, bool readOnly) =>
        NativeMethods.srui_ui_set_editbox_read_only(Handle, node.Value, readOnly);

    /// <summary>-1 when the node is not a listbox.</summary>
    public long ListboxSelected(NodeId node) => NativeMethods.srui_ui_listbox_selected(Handle, node.Value);

    public string? ListboxSelectedItem(NodeId node) =>
        NativeMethods.TakeString(NativeMethods.srui_ui_listbox_selected_item(Handle, node.Value));

    public void SetListItems(NodeId node, IReadOnlyList<string> items)
    {
        var ptrs = NativeMethods.ToUtf8Array(items);
        try
        {
            NativeMethods.srui_ui_set_list_items(Handle, node.Value, ptrs, (nuint)items.Count);
        }
        finally
        {
            NativeMethods.FreeUtf8Array(ptrs);
        }
    }

    public string? FilterSelectedItem(NodeId node) =>
        NativeMethods.TakeString(NativeMethods.srui_ui_filter_selected_item(Handle, node.Value));

    public int SliderValue(NodeId node) => NativeMethods.srui_ui_slider_value(Handle, node.Value);

    public void SetSliderValue(NodeId node, int value) =>
        NativeMethods.srui_ui_set_slider_value(Handle, node.Value, value);

    /// <summary>-1 when the node is not a tab control.</summary>
    public long TabActive(NodeId node) => NativeMethods.srui_ui_tab_active(Handle, node.Value);

    /// <summary>The captured combo in config form ("ctrl+shift+s"), or null.</summary>
    public string? ShortcutCombo(NodeId node) =>
        NativeMethods.TakeString(NativeMethods.srui_ui_shortcut_combo(Handle, node.Value));

    // ── Output ──

    // Shared result for empty drains: the host loop drains every
    // iteration and is almost always empty, so returning a fresh list
    // per call would put a steady drip of garbage under an idle app. The
    // concrete List return type (rather than IReadOnlyList) keeps
    // foreach on the struct enumerator, which is also allocation-free.
    private static readonly List<OutputEvent> EmptyBatch = new();

    /// <summary>Drain the coalesced output queue. Treat the result as
    /// read-only: empty batches are shared.</summary>
    public unsafe List<OutputEvent> Drain()
    {
        NativeMethods.srui_ui_drain(Handle, out var events, out var len);
        try
        {
            if (len == 0)
                return EmptyBatch;
            var result = new List<OutputEvent>((int)len);
            for (nuint i = 0; i < len; i++)
            {
                var e = events[i];
                var node = new NodeId(e.Node);
                OutputEvent? mapped = e.Kind switch
                {
                    1 => e.Speech != IntPtr.Zero
                        ? new OutputEvent.Speech(
                            System.Runtime.InteropServices.Marshal.PtrToStringUTF8(e.Speech)!,
                            (SpeechSource)e.Num0,
                            node)
                        : null, // rendered to silence
                    100 => new OutputEvent.Activated(node),
                    101 => new OutputEvent.SecondaryActivated(node),
                    102 => new OutputEvent.Toggled(node, e.Num0 != 0),
                    103 => new OutputEvent.Changed(node),
                    200 => new OutputEvent.Tick((ulong)e.Num0),
                    _ => null,
                };
                if (mapped is not null) result.Add(mapped);
            }
            return result;
        }
        finally
        {
            NativeMethods.srui_events_free(events, len);
        }
    }
}
