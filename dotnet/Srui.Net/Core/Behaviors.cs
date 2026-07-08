namespace Srui.Core;

/// <summary>Everything a widget behavior may touch while handling input:
/// its own node id, its label (kept in sync with widget state), the
/// output queue, the host-supplied monotonic clock, and the injected
/// clipboard.</summary>
internal readonly struct WidgetCtx
{
    public readonly NodeId Node;
    public readonly WidgetLabel Label;
    public readonly List<CoreEvent> Events;
    public readonly ulong NowMs;
    public readonly IClipboard Clipboard;

    public WidgetCtx(NodeId node, WidgetLabel label, List<CoreEvent> events, ulong nowMs, IClipboard clipboard)
    {
        Node = node;
        Label = label;
        Events = events;
        NowMs = nowMs;
        Clipboard = clipboard;
    }

    public void EmitWidget(CoreEvent ev) => Events.Add(ev);

    public void EmitAccessibility(AccessibilityEvent ev) => Events.Add(new CoreEvent.Acc(ev));

    public void Announce(string text) => EmitAccessibility(new AccessibilityEvent.Announce(text));
}

/// <summary>Behavior attached to a node: owns the node's interaction
/// state and handles logical input directed at the node while it is
/// focused. Built-in behaviors live here; hosts extend by composing
/// primitives or subclassing the public wrapper classes.</summary>
internal abstract class WidgetBehavior
{
    /// <summary>Handle a logical input directed at this node while
    /// focused. True if consumed; unconsumed input falls through to
    /// bindings and framework navigation.</summary>
    public abstract bool HandleInput(in InputEvent input, in WidgetCtx ctx);
}

/// <summary>Button — Enter or Space activates.</summary>
internal sealed class ButtonBehavior : WidgetBehavior
{
    public override bool HandleInput(in InputEvent input, in WidgetCtx ctx)
    {
        switch (input.Kind)
        {
            case InputKind.Activate:
            case InputKind.TypeChar when (char)input.Ch == ' ':
                ctx.EmitWidget(new CoreEvent.Activated(ctx.Node));
                return true;
            case InputKind.SecondaryActivate:
                ctx.EmitWidget(new CoreEvent.SecondaryActivated(ctx.Node));
                return true;
            default:
                return false;
        }
    }
}

/// <summary>CheckBox — Space toggles. Enter deliberately falls through so
/// it can reach the layer's primary widget (Windows dialog convention).</summary>
internal sealed class CheckBoxBehavior : WidgetBehavior
{
    public bool Checked;

    public CheckBoxBehavior(bool isChecked) => Checked = isChecked;

    /// <summary>The label value for a given checked state.</summary>
    public static string ValueText(bool isChecked) => isChecked ? "checked" : "not checked";

    public override bool HandleInput(in InputEvent input, in WidgetCtx ctx)
    {
        if (input.Kind == InputKind.TypeChar && (char)input.Ch == ' ')
        {
            Checked = !Checked;
            ctx.Label.Value = ValueText(Checked);
            ctx.EmitWidget(new CoreEvent.Toggled(ctx.Node, Checked));
            ctx.Announce(ValueText(Checked));
            return true;
        }
        return false;
    }
}

/// <summary>Slider — arrows adjust by the small step, Shift+arrows and
/// PageUp/PageDown by the large step, Home/End jump to the range edges.
/// Adjustments at a range edge re-announce the clamped value.</summary>
internal sealed class SliderBehavior : WidgetBehavior
{
    private int _value;
    private readonly int _min;
    private readonly int _max;
    private readonly int _smallStep;
    private readonly int _largeStep;
    /// <summary>Spoken and displayed immediately after the value ("%" → "50%").</summary>
    private readonly string _unit;

    public SliderBehavior(int value, int min, int max, int smallStep = 1, int largeStep = 10, string unit = "")
    {
        _min = min;
        _max = max;
        _value = Math.Clamp(value, min, max);
        _smallStep = smallStep;
        _largeStep = largeStep;
        _unit = unit;
    }

    public int Value => _value;

    public void SetValue(int value, WidgetLabel label)
    {
        _value = Math.Clamp(value, _min, _max);
        SyncLabel(label);
    }

    public void SyncLabel(WidgetLabel label) => label.Value = $"{_value}{_unit}";

    /// <summary>The value-change announcement, shared between user-driven
    /// adjustment and programmatic SetSliderValue.</summary>
    public AccessibilityEvent ChangeEvent(NodeId node) =>
        new AccessibilityEvent.SliderChange(node, _value, _unit);

    private void Adjust(int delta, in WidgetCtx ctx)
    {
        _value = Math.Clamp(_value + delta, _min, _max);
        ctx.EmitAccessibility(ChangeEvent(ctx.Node));
    }

    public override bool HandleInput(in InputEvent input, in WidgetCtx ctx)
    {
        var prev = _value;
        int? delta;
        switch (input.Kind)
        {
            case InputKind.MoveRight or InputKind.MoveUp:
                delta = _smallStep;
                break;
            case InputKind.MoveLeft or InputKind.MoveDown:
                delta = -_smallStep;
                break;
            case InputKind.SelectRight or InputKind.SelectLineUp:
                delta = _largeStep;
                break;
            case InputKind.SelectLeft or InputKind.SelectLineDown:
                delta = -_largeStep;
                break;
            case InputKind.MoveToLineStart:
                _value = _min;
                ctx.EmitAccessibility(ChangeEvent(ctx.Node));
                delta = null;
                break;
            case InputKind.MoveToLineEnd:
                _value = _max;
                ctx.EmitAccessibility(ChangeEvent(ctx.Node));
                delta = null;
                break;
            case InputKind.RawKey when (input.Mods & (Mods.Ctrl | Mods.Alt)) == 0:
                if (input.Key == Key.PageUp.Code)
                    delta = _largeStep;
                else if (input.Key == Key.PageDown.Code)
                    delta = -_largeStep;
                else
                    return false;
                break;
            default:
                return false;
        }
        if (delta is int d)
            Adjust(d, ctx);
        SyncLabel(ctx.Label);
        if (_value != prev)
            ctx.EmitWidget(new CoreEvent.Changed(ctx.Node));
        return true;
    }
}

/// <summary>TabControl — Left/Right cycle through tabs with wraparound.</summary>
internal sealed class TabControlBehavior : WidgetBehavior
{
    private readonly List<string> _tabs;
    private int _active;

    public TabControlBehavior(List<string> tabs, int active)
    {
        _tabs = tabs;
        _active = tabs.Count == 0 ? 0 : Math.Min(active, tabs.Count - 1);
    }

    public int Active => _active;

    public string? ActiveTab => _active < _tabs.Count ? _tabs[_active] : null;

    public void SetActive(int index, WidgetLabel label)
    {
        if (_tabs.Count == 0)
            return;
        _active = Math.Min(index, _tabs.Count - 1);
        SyncLabel(label);
    }

    public void SyncLabel(WidgetLabel label)
    {
        if (_active < _tabs.Count)
            label.Value = _tabs[_active];
    }

    /// <summary>The tab-change announcement, shared between user-driven
    /// switching and programmatic SetActiveTab. Null with no tabs.</summary>
    public AccessibilityEvent? ChangeEvent(NodeId node) => _active < _tabs.Count
        ? new AccessibilityEvent.TabChange(node, _tabs[_active], (_active, _tabs.Count))
        : null;

    private void Switch(int to, in WidgetCtx ctx)
    {
        _active = to;
        SyncLabel(ctx.Label);
        if (ChangeEvent(ctx.Node) is AccessibilityEvent ev)
            ctx.EmitAccessibility(ev);
        ctx.EmitWidget(new CoreEvent.Changed(ctx.Node));
    }

    public override bool HandleInput(in InputEvent input, in WidgetCtx ctx)
    {
        if (_tabs.Count == 0)
            return false;
        switch (input.Kind)
        {
            case InputKind.MoveRight:
                Switch(_active + 1 < _tabs.Count ? _active + 1 : 0, ctx);
                return true;
            case InputKind.MoveLeft:
                Switch(_active > 0 ? _active - 1 : _tabs.Count - 1, ctx);
                return true;
            default:
                return false;
        }
    }
}

/// <summary>ShortcutField — captures whatever combo the user presses as
/// its value. Delete/Backspace clear it; Tab, Escape, and SpeakFocus pass
/// through so the user can still leave the field.</summary>
internal sealed class ShortcutFieldBehavior : WidgetBehavior
{
    private KeyCombo? _combo;
    /// <summary>When false, capturing a combo produces no speech feedback.</summary>
    public bool Echo = true;

    public KeyCombo? Combo => _combo;

    public void SetCombo(KeyCombo? combo, WidgetLabel label)
    {
        _combo = combo;
        SyncLabel(label);
    }

    public void SyncLabel(WidgetLabel label) =>
        label.Value = _combo is KeyCombo combo ? combo.DisplayName() : "blank";

    private void Capture(KeyCombo combo, in WidgetCtx ctx)
    {
        _combo = combo;
        SyncLabel(ctx.Label);
        if (Echo)
            SayValue(combo.DisplayName(), ctx);
        ctx.EmitWidget(new CoreEvent.Changed(ctx.Node));
    }

    /// <summary>A shortcut field has no indexable concept, so its value
    /// changes ride ItemNav with no position.</summary>
    private static void SayValue(string value, in WidgetCtx ctx) =>
        ctx.EmitAccessibility(new AccessibilityEvent.ItemNav(ctx.Node, value, null, null));

    public override bool HandleInput(in InputEvent input, in WidgetCtx ctx)
    {
        switch (input.Kind)
        {
            // Delete/Backspace clears the shortcut.
            case InputKind.DeleteBackward or InputKind.DeleteForward:
                if (_combo is not null)
                {
                    _combo = null;
                    SyncLabel(ctx.Label);
                    SayValue("blank", ctx);
                    ctx.EmitWidget(new CoreEvent.Changed(ctx.Node));
                }
                return true;

            // RawKey — capture the combo directly.
            case InputKind.RawKey:
                Capture(KeyCombo.FromFlat(input.Key, input.Mods), ctx);
                return true;

            // Let Tab, Escape, and framework inputs through.
            case InputKind.NavigateNext or InputKind.NavigatePrev
                or InputKind.Dismiss or InputKind.SpeakFocus:
                return false;

            // Any other input with a combo mapping — capture it.
            default:
                if (KeyCombo.FromInput(input) is KeyCombo combo)
                {
                    if (combo.Key == Key.Tab || combo.Key == Key.Escape)
                        return false; // let through for navigation/dismiss
                    Capture(combo, ctx);
                    return true;
                }
                // Unknown input — consume silently.
                return true;
        }
    }
}

/// <summary>FilterListBox — type-to-filter list. Printable characters
/// build a fuzzy-match query, Backspace erases it, arrows and Home/End
/// navigate the filtered results. Enter is not claimed (the layer's
/// primary reads the selection).</summary>
internal sealed class FilterListBoxBehavior : WidgetBehavior
{
    private List<string> _items;
    private string _filter = "";
    private int _selected;

    public FilterListBoxBehavior(List<string> items) => _items = items;

    public string Filter => _filter;

    /// <summary>The items currently matching the filter, best match first.</summary>
    public List<string> Filtered() => Fuzzy.FilterItems(_filter, _items);

    public string? SelectedItem()
    {
        var filtered = Filtered();
        return _selected < filtered.Count ? filtered[_selected] : null;
    }

    /// <summary>Replace the full item list; the filter is kept, the
    /// selection reset.</summary>
    public void SetItems(List<string> items, WidgetLabel label)
    {
        _items = items;
        _selected = 0;
        SyncLabel(label);
    }

    /// <summary>Clear the filter and selection.</summary>
    public void ClearFilter(WidgetLabel label)
    {
        _filter = "";
        _selected = 0;
        SyncLabel(label);
    }

    /// <summary>Label value mirrors the selected result; state text
    /// carries the filter ("no filter" / "filter {query}").</summary>
    public void SyncLabel(WidgetLabel label)
    {
        var filtered = Filtered();
        label.Value = _selected < filtered.Count ? filtered[_selected] : "empty";
        label.StateText = _filter.Length == 0 ? "no filter" : $"filter {_filter}";
    }

    private void EmitItem(List<string> filtered, in WidgetCtx ctx, Boundary? boundary) =>
        ctx.EmitAccessibility(new AccessibilityEvent.ItemNav(
            ctx.Node, filtered[_selected], (_selected, filtered.Count), boundary));

    private void SelectAndAnnounce(List<string> filtered, int index, in WidgetCtx ctx)
    {
        _selected = index;
        SyncLabel(ctx.Label);
        EmitItem(filtered, ctx, null);
        ctx.EmitWidget(new CoreEvent.Changed(ctx.Node));
    }

    /// <summary>Filter text changed: reset the selection and report the
    /// new results.</summary>
    private void FilterChanged(in WidgetCtx ctx)
    {
        _selected = 0;
        SyncLabel(ctx.Label);
        var filtered = Filtered();
        ctx.EmitAccessibility(new AccessibilityEvent.Filter(
            ctx.Node, _filter, filtered.Count > 0 ? filtered[0] : null, filtered.Count));
        ctx.EmitWidget(new CoreEvent.Changed(ctx.Node));
    }

    public override bool HandleInput(in InputEvent input, in WidgetCtx ctx)
    {
        var filtered = Filtered();
        switch (input.Kind)
        {
            case InputKind.MoveDown when filtered.Count > 0:
                if (_selected + 1 < filtered.Count)
                    SelectAndAnnounce(filtered, _selected + 1, ctx);
                else
                    EmitItem(filtered, ctx, Boundary.Bottom);
                return true;
            case InputKind.MoveUp when filtered.Count > 0:
                if (_selected > 0)
                    SelectAndAnnounce(filtered, _selected - 1, ctx);
                else
                    EmitItem(filtered, ctx, Boundary.Top);
                return true;
            case InputKind.MoveToDocStart or InputKind.MoveToLineStart when filtered.Count > 0:
                if (_selected != 0)
                    SelectAndAnnounce(filtered, 0, ctx);
                return true;
            case InputKind.MoveToDocEnd or InputKind.MoveToLineEnd when filtered.Count > 0:
                if (_selected != filtered.Count - 1)
                    SelectAndAnnounce(filtered, filtered.Count - 1, ctx);
                return true;
            case InputKind.TypeChar:
                if (System.Text.Rune.IsValid((int)input.Ch))
                    _filter += ListBoxBehavior.AsciiLowerString(char.ConvertFromUtf32((int)input.Ch));
                FilterChanged(ctx);
                return true;
            case InputKind.DeleteBackward when _filter.Length > 0:
                // Remove one character — two units when it is astral.
                var cut = _filter.Length >= 2 && char.IsLowSurrogate(_filter[^1]) ? 2 : 1;
                _filter = _filter[..^cut];
                FilterChanged(ctx);
                return true;
            default:
                return false;
        }
    }
}

/// <summary>ListBox — a single-selection list. Arrows move the selection
/// with boundary announcements, Home/End jump, printable characters do
/// first-letter cycling and multi-letter prefix search. Enter is
/// deliberately not claimed: it falls through to the layer's primary
/// widget (Windows dialog convention).</summary>
internal sealed class ListBoxBehavior : WidgetBehavior
{
    /// <summary>Timeout for resetting the typeahead buffer (milliseconds
    /// of host time).</summary>
    private const ulong TypeAheadTimeoutMs = 400;

    private List<string> _items;
    private int _selected;
    /// <summary>When true, announcements and focus state text carry "N of M".</summary>
    private readonly bool _numbered;
    private string _typeAheadBuffer = "";
    private ulong? _lastKeystrokeMs;

    public ListBoxBehavior(List<string> items, bool numbered)
    {
        _items = items;
        _numbered = numbered;
    }

    public IReadOnlyList<string> Items => _items;

    public int Selected => _selected;

    public string? SelectedItem => _selected < _items.Count ? _items[_selected] : null;

    /// <summary>Replace the item list, clamping the selection.</summary>
    public void SetItems(List<string> items, WidgetLabel label)
    {
        _items = items;
        if (_items.Count > 0 && _selected >= _items.Count)
            _selected = _items.Count - 1;
        SyncLabel(label);
    }

    /// <summary>Move the selection programmatically (clamped).</summary>
    public void SetSelected(int index, WidgetLabel label)
    {
        if (_items.Count == 0)
            return;
        _selected = Math.Min(index, _items.Count - 1);
        SyncLabel(label);
    }

    /// <summary>Keep the golden six in step with the widget state: value
    /// is the selected item (or "empty"), state text is "N of M" when
    /// numbered.</summary>
    public void SyncLabel(WidgetLabel label)
    {
        if (_selected < _items.Count)
        {
            label.Value = _items[_selected];
            if (_numbered)
                label.StateText = $"{_selected + 1} of {_items.Count}";
        }
        else
        {
            label.Value = "empty";
            if (_numbered)
                label.StateText = "";
        }
    }

    /// <summary>The selection announcement, shared between user-driven
    /// navigation and programmatic SetListSelected. Null when empty.</summary>
    public AccessibilityEvent? ChangeEvent(NodeId node, Boundary? boundary)
    {
        if (_selected >= _items.Count)
            return null;
        (int, int)? position = _numbered ? (_selected, _items.Count) : null;
        return new AccessibilityEvent.ItemNav(node, _items[_selected], position, boundary);
    }

    private void EmitItem(in WidgetCtx ctx, Boundary? boundary)
    {
        if (ChangeEvent(ctx.Node, boundary) is AccessibilityEvent ev)
            ctx.EmitAccessibility(ev);
    }

    /// <summary>Selection moved by input: sync label, announce, notify
    /// the program.</summary>
    private void SelectAndAnnounce(int index, in WidgetCtx ctx)
    {
        _selected = index;
        SyncLabel(ctx.Label);
        EmitItem(ctx, null);
        ctx.EmitWidget(new CoreEvent.Changed(ctx.Node));
    }

    private void HandleTypeAhead(string runeText, in WidgetCtx ctx)
    {
        var runeLower = AsciiLowerString(runeText);

        var shouldReset = _lastKeystrokeMs is not ulong last
            || ctx.NowMs - Math.Min(ctx.NowMs, last) > TypeAheadTimeoutMs;
        // Cycling: the same letter typed repeatedly.
        var cycling = _typeAheadBuffer.Length > 0 && IsRepeatsOf(_typeAheadBuffer, runeLower);

        if (shouldReset || cycling)
            _typeAheadBuffer = "";
        _typeAheadBuffer += runeLower;
        _lastKeystrokeMs = ctx.NowMs;

        if (cycling || _typeAheadBuffer == runeLower)
        {
            // Single char: cycle from the current position forward.
            var count = _items.Count;
            for (var offset = 1; offset <= count; offset++)
            {
                var idx = (_selected + offset) % count;
                if (StartsWithAsciiLower(_items[idx], runeLower))
                {
                    SelectAndAnnounce(idx, ctx);
                    break;
                }
            }
        }
        else
        {
            // Multi-letter prefix search with wraparound, current item included.
            var needle = _typeAheadBuffer;
            var count = _items.Count;
            for (var offset = 0; offset < count; offset++)
            {
                var idx = (_selected + offset) % count;
                if (StartsWithAsciiLower(_items[idx], needle))
                {
                    if (idx != _selected)
                        SelectAndAnnounce(idx, ctx);
                    else
                        EmitItem(ctx, null);
                    break;
                }
            }
        }
    }

    internal static string AsciiLowerString(string s)
    {
        var chars = s.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            chars[i] = ToAsciiLower(chars[i]);
        return new string(chars);
    }

    private static bool IsRepeatsOf(string s, string unit)
    {
        if (unit.Length == 0 || s.Length % unit.Length != 0)
            return false;
        for (var i = 0; i < s.Length; i += unit.Length)
            if (string.CompareOrdinal(s, i, unit, 0, unit.Length) != 0)
                return false;
        return true;
    }

    private static bool StartsWithAsciiLower(string item, string needle)
    {
        if (item.Length < needle.Length)
            return false;
        for (var i = 0; i < needle.Length; i++)
            if (ToAsciiLower(item[i]) != needle[i])
                return false;
        return true;
    }

    public override bool HandleInput(in InputEvent input, in WidgetCtx ctx)
    {
        if (_items.Count == 0)
            return false;
        switch (input.Kind)
        {
            case InputKind.MoveDown:
                if (_selected + 1 < _items.Count)
                    SelectAndAnnounce(_selected + 1, ctx);
                else
                    EmitItem(ctx, Boundary.Bottom);
                return true;
            case InputKind.MoveUp:
                if (_selected > 0)
                    SelectAndAnnounce(_selected - 1, ctx);
                else
                    EmitItem(ctx, Boundary.Top);
                return true;
            case InputKind.MoveToDocStart or InputKind.MoveToLineStart:
                if (_selected != 0)
                    SelectAndAnnounce(0, ctx);
                return true;
            case InputKind.MoveToDocEnd or InputKind.MoveToLineEnd:
                if (_selected != _items.Count - 1)
                    SelectAndAnnounce(_items.Count - 1, ctx);
                return true;
            case InputKind.TypeChar:
                if (System.Text.Rune.IsValid((int)input.Ch))
                    HandleTypeAhead(char.ConvertFromUtf32((int)input.Ch), ctx);
                return true;
            default:
                return false;
        }
    }

    private static char ToAsciiLower(char c) => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
}
