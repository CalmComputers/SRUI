using Srui.Core;

namespace Srui;

/// <summary>
/// The retained accessibility tree and input dispatcher. Build widgets,
/// feed input, drain output events. Not thread-safe: one Ui, one thread.
/// Node handles are plain values, so C#-side widget classes compose
/// freely over them.
/// </summary>
public sealed class Ui : IDisposable
{
    private readonly CoreUi _core = new();

    internal CoreUi Core => _core;

    /// <summary>Nothing unmanaged to release; kept for API stability.</summary>
    public void Dispose()
    {
    }

    internal void InstallClipboard(IClipboard clipboard) => _core.SetClipboard(clipboard);

    // ── Clock, focus, input ──

    /// <summary>Advance the monotonic clock (drives typeahead timeouts).</summary>
    public void SetNow(ulong milliseconds) => _core.SetNow(milliseconds);

    /// <summary>Focus the first focusable node if nothing is focused.</summary>
    public bool EnsureFocus() => _core.EnsureFocus();

    public void SetFocus(NodeId node) => _core.SetFocus(node);

    public NodeId Focus => _core.Focus;

    /// <summary>Dispatch one input. Returns false when unconsumed — the
    /// host's own bindings get it then.</summary>
    public bool HandleInput(in InputEvent input) => _core.HandleInput(input);

    /// <summary>Queue a free-form announcement for the readers.</summary>
    public void Announce(string text) => _core.Announce(text);

    /// <summary>Re-announce the focused node with its context labels
    /// (preceding Label siblings) — the dialog-open announcement.</summary>
    public void ReannounceWithContext() => _core.ReannounceWithContext();

    /// <summary>Register a periodic ticker; Tick events fire at SetNow
    /// resolution. Returns the id carried by the events.</summary>
    public ulong AddTicker(ulong intervalMs) => _core.AddTicker(intervalMs);

    public void RemoveTicker(ulong id) => _core.RemoveTicker(id);

    // ── Layers and defaults ──

    public void SetPrimary(NodeId node) => _core.SetPrimary(node);
    public void SetCancel(NodeId node) => _core.SetCancel(node);
    public void PushLayer() => _core.PushLayer();
    public void PopLayer() => _core.PopLayer();
    public void Remove(NodeId node) => _core.Remove(node);

    /// <summary>Hide/show a node and its subtree. Focus recovers (with
    /// an announcement) if it was inside.</summary>
    public void SetHidden(NodeId node, bool hidden) => _core.SetHidden(node, hidden);

    /// <summary>Enable/disable a node. Focus recovers if it was here.</summary>
    public void SetDisabled(NodeId node, bool disabled) => _core.SetDisabled(node, disabled);

    /// <summary>Rename a node; re-announces when focused.</summary>
    public void SetNodeName(NodeId node, string name) => _core.SetNodeName(node, name);

    /// <summary>Change a node's spoken description.</summary>
    public void SetNodeDescription(NodeId node, string description) =>
        _core.SetNodeDescription(node, description);

    /// <summary>Attach a shortcut to a node. <paramref name="combo"/> is
    /// the config form ("ctrl+shift+s"). Returns false when the combo
    /// fails to parse.</summary>
    public bool AddShortcut(NodeId node, string combo, ShortcutAction action)
    {
        if (!KeyCombo.TryParseConfig(combo, out var parsed))
            return false;
        _core.AddShortcut(node, parsed, action);
        return true;
    }

    /// <summary>Remove every shortcut from a node.</summary>
    public void ClearShortcuts(NodeId node) => _core.ClearShortcuts(node);

    // ── Widgets ──

    public NodeId TextLabel(NodeId parent, string text) => _core.TextLabel(parent, text);

    public NodeId Group(NodeId parent, string name) => _core.Group(parent, name);

    /// <summary>A custom widget: focusable, no spoken role, no built-in
    /// behavior — every key falls through to the host's bindings.</summary>
    public NodeId Custom(NodeId parent, string name) => _core.Custom(parent, name);

    public NodeId Button(NodeId parent, string name) => _core.Button(parent, name);

    public NodeId Checkbox(NodeId parent, string name, bool isChecked) =>
        _core.Checkbox(parent, name, isChecked);

    public NodeId Editbox(NodeId parent, string name, string text = "", bool multiline = false) =>
        _core.Editbox(parent, name, text, multiline);

    public NodeId Listbox(NodeId parent, string name, IReadOnlyList<string> items, bool numbered = false) =>
        _core.Listbox(parent, name, new List<string>(items), numbered);

    public NodeId FilterListbox(NodeId parent, string name, IReadOnlyList<string> items) =>
        _core.FilterListbox(parent, name, new List<string>(items));

    public NodeId Slider(
        NodeId parent, string name, int value, int min, int max,
        int smallStep = 1, int largeStep = 10, string unit = "") =>
        _core.SliderWidget(parent, name, new SliderBehavior(value, min, max, smallStep, largeStep, unit));

    public NodeId TabControl(NodeId parent, string name, IReadOnlyList<string> tabs, int active = 0) =>
        _core.TabControl(parent, name, new List<string>(tabs), active);

    public NodeId ShortcutField(NodeId parent, string name) => _core.ShortcutField(parent, name);

    // ── Widget state ──

    public bool CheckboxChecked(NodeId node) =>
        _core.Widget<CheckBoxBehavior>(node)?.Checked ?? false;

    public string EditboxText(NodeId node) =>
        _core.Widget<EditBoxBehavior>(node)?.Text ?? "";

    public void SetEditboxText(NodeId node, string text) => _core.SetEditboxText(node, text);

    public void SetEditboxReadOnly(NodeId node, bool readOnly) =>
        _core.SetEditboxReadOnly(node, readOnly);

    /// <summary>-1 when the node is not a listbox (or is empty).</summary>
    public long ListboxSelected(NodeId node) =>
        _core.Widget<ListBoxBehavior>(node) is ListBoxBehavior list && list.Items.Count > 0
            ? list.Selected
            : -1;

    public string? ListboxSelectedItem(NodeId node) =>
        _core.Widget<ListBoxBehavior>(node)?.SelectedItem;

    public void SetListItems(NodeId node, IReadOnlyList<string> items) =>
        _core.SetListItems(node, new List<string>(items));

    public string? FilterSelectedItem(NodeId node) =>
        _core.Widget<FilterListBoxBehavior>(node)?.SelectedItem();

    public int SliderValue(NodeId node) => _core.Widget<SliderBehavior>(node)?.Value ?? 0;

    public void SetSliderValue(NodeId node, int value) => _core.SetSliderValue(node, value);

    /// <summary>-1 when the node is not a tab control.</summary>
    public long TabActive(NodeId node) =>
        _core.Widget<TabControlBehavior>(node) is TabControlBehavior tabs ? tabs.Active : -1;

    /// <summary>The captured combo in config form ("ctrl+shift+s"), or null.</summary>
    public string? ShortcutCombo(NodeId node) =>
        _core.Widget<ShortcutFieldBehavior>(node)?.Combo?.ToConfigString();

    // ── Output ──

    // Shared result for empty drains: the host loop drains every
    // iteration and is almost always empty, so returning a fresh list
    // per call would put a steady drip of garbage under an idle app. The
    // concrete List return type (rather than IReadOnlyList) keeps
    // foreach on the struct enumerator, which is also allocation-free.
    private static readonly List<OutputEvent> EmptyBatch = new();

    /// <summary>Drain the coalesced output queue. Treat the result as
    /// read-only: empty batches are shared.</summary>
    public List<OutputEvent> Drain()
    {
        var batch = _core.DrainEvents();
        if (batch.Count == 0)
            return EmptyBatch;
        var result = new List<OutputEvent>(batch.Count);
        foreach (var ev in batch)
        {
            switch (ev)
            {
                case CoreEvent.Acc(var acc):
                    // Rendered to silence → skipped, same as the readers.
                    if (SpeechRenderer.RenderEvent(acc) is string text)
                        result.Add(new OutputEvent.Speech(
                            text, SpeechRenderer.SourceOf(acc), SpeechRenderer.NodeOf(acc)));
                    break;
                case CoreEvent.Activated(var node):
                    result.Add(new OutputEvent.Activated(node));
                    break;
                case CoreEvent.SecondaryActivated(var node):
                    result.Add(new OutputEvent.SecondaryActivated(node));
                    break;
                case CoreEvent.Toggled(var node, var isChecked):
                    result.Add(new OutputEvent.Toggled(node, isChecked));
                    break;
                case CoreEvent.Changed(var node):
                    result.Add(new OutputEvent.Changed(node));
                    break;
                case CoreEvent.Tick(var ticker):
                    result.Add(new OutputEvent.Tick(ticker));
                    break;
            }
        }
        return result;
    }
}
