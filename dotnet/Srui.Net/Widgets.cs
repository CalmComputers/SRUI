namespace Srui;

/// <summary>Static text. Precedes the widget it describes in the tree.</summary>
public class Label : Widget
{
    public Label(IWidgetContainer parent, string text) : base(parent)
    {
        Node = App.Ui.TextLabel(parent.ContainerNode, text);
        Register();
    }
}

/// <summary>A container; children are created with it as their parent.
/// Alt+Down enters it, Alt+Up leaves.</summary>
public class Group : Widget, IWidgetContainer
{
    public Group(IWidgetContainer parent, string name) : base(parent)
    {
        Node = App.Ui.Group(parent.ContainerNode, name);
        Register();
    }

    NodeId IWidgetContainer.ContainerNode => Node;
}

/// <summary>Enter or Space presses it (or Enter anywhere, as the layer's
/// primary; Escape anywhere, as the cancel).</summary>
public class Button : Widget
{
    public Button(IWidgetContainer parent, string name) : base(parent)
    {
        Node = App.Ui.Button(parent.ContainerNode, name);
        Register();
    }

    public event Action? Activated;
    public event Action? SecondaryActivated;

    protected internal virtual void OnActivated() => Activated?.Invoke();
    protected internal virtual void OnSecondaryActivated() => SecondaryActivated?.Invoke();
}

/// <summary>Space toggles; Enter falls through to the layer's primary.</summary>
public class CheckBox : Widget
{
    public CheckBox(IWidgetContainer parent, string name, bool isChecked = false) : base(parent)
    {
        Node = App.Ui.Checkbox(parent.ContainerNode, name, isChecked);
        Register();
    }

    public bool Checked => Ui.CheckboxChecked(Node);

    public event Action<bool>? Toggled;

    protected internal virtual void OnToggled(bool isChecked) => Toggled?.Invoke(isChecked);
}

/// <summary>Single- or multi-line text editor with full cursor
/// navigation, selection, and clipboard.</summary>
public class EditBox : Widget
{
    public EditBox(IWidgetContainer parent, string name, string text = "", bool multiline = false)
        : base(parent)
    {
        Node = App.Ui.Editbox(parent.ContainerNode, name, text, multiline);
        Register();
    }

    public string Text
    {
        get => Ui.EditboxText(Node);
        set => Ui.SetEditboxText(Node, value);
    }

    private bool _readOnly;

    public bool ReadOnly
    {
        get => _readOnly;
        set
        {
            _readOnly = value;
            Ui.SetEditboxReadOnly(Node, value);
        }
    }
}

/// <summary>Single-selection list with arrow navigation and typeahead.
/// Enter falls through to the layer's primary, which reads the
/// selection.</summary>
public class ListBox : Widget
{
    public ListBox(
        IWidgetContainer parent, string name, IReadOnlyList<string> items, bool numbered = false)
        : base(parent)
    {
        Node = App.Ui.Listbox(parent.ContainerNode, name, items, numbered);
        Register();
    }

    /// <summary>-1 when empty.</summary>
    public int SelectedIndex => (int)Ui.ListboxSelected(Node);

    public string? SelectedItem => Ui.ListboxSelectedItem(Node);

    public void SetItems(IReadOnlyList<string> items) => Ui.SetListItems(Node, items);
}

/// <summary>Type-to-filter list: printable characters build a fuzzy
/// query, Backspace erases, arrows navigate the results.</summary>
public class FilterListBox : Widget
{
    public FilterListBox(IWidgetContainer parent, string name, IReadOnlyList<string> items)
        : base(parent)
    {
        Node = App.Ui.FilterListbox(parent.ContainerNode, name, items);
        Register();
    }

    public string? SelectedItem => Ui.FilterSelectedItem(Node);
}

/// <summary>Arrows adjust by the small step, Shift+arrows and
/// PageUp/PageDown by the large step, Home/End jump to the edges.</summary>
public class Slider : Widget
{
    public Slider(
        IWidgetContainer parent, string name, int value, int min, int max,
        int smallStep = 1, int largeStep = 10, string unit = "")
        : base(parent)
    {
        Node = App.Ui.Slider(parent.ContainerNode, name, value, min, max, smallStep, largeStep, unit);
        Register();
    }

    public int Value
    {
        get => Ui.SliderValue(Node);
        set => Ui.SetSliderValue(Node, value);
    }
}

/// <summary>Left/Right cycle through tabs with wraparound.</summary>
public class TabControl : Widget
{
    public TabControl(IWidgetContainer parent, string name, IReadOnlyList<string> tabs, int active = 0)
        : base(parent)
    {
        Node = App.Ui.TabControl(parent.ContainerNode, name, tabs, active);
        Register();
    }

    public int ActiveIndex => (int)Ui.TabActive(Node);
}

/// <summary>Captures whatever combo the user presses; Delete clears.
/// Tab and Escape still leave the field.</summary>
public class ShortcutField : Widget
{
    public ShortcutField(IWidgetContainer parent, string name) : base(parent)
    {
        Node = App.Ui.ShortcutField(parent.ContainerNode, name);
        Register();
    }

    /// <summary>The captured combo in config form ("ctrl+shift+s"), or null.</summary>
    public string? Combo => Ui.ShortcutCombo(Node);
}
