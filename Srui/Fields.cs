namespace Srui;

/// <summary>A typed key naming one thing a reader can know about a
/// widget or an item: its name, its role, whether it is checked, the
/// number a slider holds. Keys are declared once and compared by
/// reference; the core set lives in <see cref="Fields"/>, and a
/// program declares its own by putting <see cref="FieldAttribute"/>
/// on a property whose name is not a core key (the generator then
/// declares <c>{Name}Field</c> on the type). Readers render fields
/// through their own tables, so every word the user hears about a
/// field is the reader's: the engine and the widgets carry no
/// prose.</summary>
public abstract class Field
{
    /// <summary>The key's name — the property it was declared on.</summary>
    public string Name { get; }

    /// <summary>A field that speaks only when focus arrives, never as
    /// a delta: the shortcut list, whose change is structural.</summary>
    public bool FocusOnly { get; }

    /// <summary>The field this one is placed after in a reading, when
    /// the declaration says so (<see cref="FieldAttribute.After"/>) — the
    /// key's default place, which a reader may override.</summary>
    public Field? After { get; }

    /// <summary>The field this one is placed before in a reading; see
    /// <see cref="After"/>.</summary>
    public Field? Before { get; }

    private protected Field(string name, bool focusOnly, Field? after, Field? before)
    {
        Name = name;
        FocusOnly = focusOnly;
        After = after;
        Before = before;
    }

    /// <summary>Whether two boxed values of this field are the same
    /// for the purpose of the tick diff.</summary>
    internal abstract bool ValuesEqual(object? a, object? b);

    /// <inheritdoc/>
    public override string ToString() => Name;
}

/// <summary>A <see cref="Field"/> whose values are <typeparamref name="T"/>.
/// The comparer decides what counts as a change; the default is value
/// equality, and <see cref="ReferenceEqualityComparer"/> serves keys
/// whose values are identities.</summary>
public sealed class Field<T> : Field
{
    /// <summary>How values are compared for the tick diff.</summary>
    public IEqualityComparer<T> Comparer { get; }

    /// <summary>Declare a key. A program's own keys are normally
    /// declared by the generator, not by hand.</summary>
    public Field(string name, bool focusOnly = false, IEqualityComparer<T>? comparer = null,
        Field? after = null, Field? before = null)
        : base(name, focusOnly, after, before)
    {
        Comparer = comparer ?? EqualityComparer<T>.Default;
    }

    internal override bool ValuesEqual(object? a, object? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        return Comparer.Equals((T)a, (T)b);
    }
}

/// <summary>Where a field was read from in a tick: the focused widget
/// itself, or the item under its cursor (<see cref="Widget.CurrentItem"/>).
/// The same key can be either — a check box's Checked is a control
/// field, a list item's is an item field — and readers may render
/// them alike or apart.</summary>
public enum FieldScope
{
    Control,
    Item,
}

/// <summary>A position among siblings: zero-based index and total.
/// Rendered "N of M" by the speech reader.</summary>
public readonly record struct Position(int Index, int Total);

/// <summary>A widget's kind, compared by reference. The core roles
/// are here; a program declares its own (<c>new Role("table")</c>) for
/// a widget authored from the base. Readers map roles to words, so a
/// role's name is an identifier, not what is spoken. A role may imply
/// fields: ones its word already says (a console's "output" is read
/// only and many lines by nature), which readers then leave unspoken
/// while the fields stay present and readable.</summary>
public sealed class Role
{
    /// <summary>The identifier — the reader's key into its role table,
    /// and its fallback wording for a role it does not know.</summary>
    public string Name { get; }

    /// <summary>The fields the role's word already states, which
    /// readers leave unspoken on widgets of this role.</summary>
    public IReadOnlyList<Field> Implies { get; }

    public Role(string name, params Field[] implies)
    {
        Name = name;
        Implies = implies;
    }

    /// <summary>No role: the widget announces as its name alone — game
    /// surfaces and bespoke controls whose identity is their name.</summary>
    public static readonly Role None = new("");
    public static readonly Role Label = new("label");
    public static readonly Role Group = new("group");
    public static readonly Role Button = new("button");
    public static readonly Role CheckBox = new("check box");
    public static readonly Role Edit = new("edit");
    public static readonly Role List = new("list");
    public static readonly Role Grid = new("grid");
    /// <summary>A type-to-filter list; spoken like a list, with its
    /// own empty wording ("no results").</summary>
    public static readonly Role FilterList = new("filter list");
    public static readonly Role Tree = new("tree view");
    public static readonly Role Slider = new("slider");
    public static readonly Role Tabs = new("tab control");
    public static readonly Role ShortcutField = new("shortcut field");

    /// <inheritdoc/>
    public override string ToString() => Name;
}

/// <summary>The core field keys. A [Field] property whose name matches
/// one of these binds to it (the property type must convert to the
/// key's); any other name declares a key of its own on the declaring
/// type. A nullable key is one that can be absent — a widget with no
/// name, an item with no checked concept, a list that does not count —
/// and null is the only spelling of absence: an empty string is a
/// value (an empty edit box, an empty query).</summary>
public static class Fields
{
    // ── Every widget ──

    /// <summary>The spoken name; absent announces as role and value only.</summary>
    public static readonly Field<string?> Name = new("Name");
    public static readonly Field<Role> Role = new("Role", comparer: ReferenceEqualityComparer.Instance);
    /// <summary>Explanatory text for an esoteric name — never keys or
    /// actions, which belong in <see cref="Widget.KeyHelp"/>.</summary>
    public static readonly Field<string> Description = new("Description");
    /// <summary>The widget's shortcuts, first added first; announcements
    /// speak the first. Structural: speaks with focus, never as a delta.</summary>
    public static readonly Field<IReadOnlyList<KeyCombo>> Shortcuts =
        new("Shortcuts", focusOnly: true, comparer: new SequenceComparer<KeyCombo>());
    public static readonly Field<bool> Disabled = new("Disabled");
    public static readonly Field<bool> Required = new("Required");
    public static readonly Field<bool> Warning = new("Warning");
    /// <summary>The widget carries <see cref="Widget.KeyHelp"/> and
    /// announces it ("with help").</summary>
    public static readonly Field<bool> WithHelp = new("WithHelp");

    // ── Lists and trees ──

    /// <summary>Items are checked independently of the cursor.</summary>
    public static readonly Field<bool> MultiSelect = new("MultiSelect");
    /// <summary>How many items the widget holds; zero is what a reader
    /// renders as empty.</summary>
    public static readonly Field<int> Count = new("Count");
    /// <summary>The cursor's position among its siblings, when the
    /// widget counts.</summary>
    public static readonly Field<Position?> Position = new("Position");
    /// <summary>The cell under a grid's cursor: row, column, and the
    /// name the grid's scheme gives it, which is what speech says.
    /// Read after the item, as a position is.</summary>
    public static readonly Field<Cell?> Coordinate = new("Coordinate", after: Position);
    /// <summary>The type-to-filter query; empty when nothing is typed,
    /// which readers word as "no filter".</summary>
    public static readonly Field<string> Filter = new("Filter");

    // ── Items, and value-bearing controls ──

    /// <summary>The line: an item's text, an editor's current line.
    /// Empty is an empty line ("blank" on a control); absent is an
    /// element with no line at all.</summary>
    public static readonly Field<string?> Value = new("Value");
    /// <summary>An editor's selected text, absent when nothing is
    /// selected; readers speak it in place of the line, and may say
    /// so when it goes.</summary>
    public static readonly Field<string?> SelectedText = new("SelectedText");
    /// <summary>Checked state; absent when the thing has no checked
    /// concept. A check box speaks both states, an item speaks only
    /// "checked" — the absence is the signal.</summary>
    public static readonly Field<bool?> Checked = new("Checked");
    /// <summary>A branch's expansion; meaningless (and unspoken) when
    /// <see cref="ChildCount"/> is zero.</summary>
    public static readonly Field<bool> Expanded = new("Expanded");
    public static readonly Field<int> ChildCount = new("ChildCount");
    /// <summary>Depth in a tree, roots at zero.</summary>
    public static readonly Field<int> Level = new("Level");

    // ── Editors ──

    public static readonly Field<bool> ReadOnly = new("ReadOnly");
    public static readonly Field<bool> Multiline = new("Multiline");
    /// <summary>A password field: masked throughout, "protected" on
    /// focus.</summary>
    public static readonly Field<bool> Password = new("Password");

    // ── Numeric controls ──

    public static readonly Field<double> Number = new("Number");
    public static readonly Field<double> Min = new("Min");
    public static readonly Field<double> Max = new("Max");
    /// <summary>Spoken directly after the number ("%" → "50%").</summary>
    public static readonly Field<string?> Unit = new("Unit");

    /// <summary>Whether a field says where the cursor is (a position, a
    /// coordinate): reread with the item at an edge, and an arrival's
    /// when the item is. A position is also read in full when the
    /// cursor lands on another item; a coordinate is not, because a
    /// grid's cursor is a cell, and an item that changes under it is
    /// news while the place is not.</summary>
    internal static bool IsPlace(Field field) =>
        ReferenceEquals(field, Position) || ReferenceEquals(field, Coordinate);

    private sealed class SequenceComparer<T> : IEqualityComparer<IReadOnlyList<T>>
    {
        public bool Equals(IReadOnlyList<T>? x, IReadOnlyList<T>? y)
        {
            if (ReferenceEquals(x, y))
                return true;
            if (x is null || y is null || x.Count != y.Count)
                return false;
            for (var i = 0; i < x.Count; i++)
                if (!EqualityComparer<T>.Default.Equals(x[i], y[i]))
                    return false;
            return true;
        }

        public int GetHashCode(IReadOnlyList<T> obj) => obj.Count;
    }
}
