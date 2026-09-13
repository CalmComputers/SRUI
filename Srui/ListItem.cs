namespace Srui;

/// <summary>A plain-text item — what the string convenience overloads
/// wrap plain strings into, and the smallest possible item: a line and
/// a checked state. Application item types are ordinary
/// <see cref="Element"/> subclasses declaring whatever fields their
/// lines need; readers speak each through their own tables.</summary>
public sealed partial class ListItem : Element
{
    /// <summary>The line readers speak and typeahead matches.</summary>
    [Field] public partial string? Value { get; set; }

    /// <summary>Checked state in a multi-select list; null until
    /// touched, which readers treat as unchecked.</summary>
    [Field] public partial bool? Checked { get; set; }

    public ListItem(string text) => Value = text;

    public static implicit operator ListItem(string text) => new(text);

    public override string ToString() => Value ?? "";
}
