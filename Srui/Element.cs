using System.Diagnostics.CodeAnalysis;

namespace Srui;

/// <summary>Marks a property as a field of its element: described to
/// readers, diffed at tick end, bindable, and addressable by key. On a
/// partial property with no accessor bodies the generator supplies the
/// implementation (a stored value, or the binding when one is
/// installed); on an ordinary property it registers the property as it
/// is, which is how a computed field is declared. The key is the
/// <see cref="Fields"/> member of the same name, or a <c>{Name}Field</c>
/// the generator declares on the type.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class FieldAttribute : Attribute
{
}

/// <summary>A field's live connection to the program's model: reads
/// go to the getter, writes to the setter. A binding with no setter is
/// a display; a widget whose input would write it throws.</summary>
public sealed class Binding<T>
{
    private readonly Func<T> _get;
    private readonly Action<T>? _set;
    private readonly Field<T> _field;

    internal Binding(Field<T> field, Func<T> get, Action<T>? set)
    {
        _field = field;
        _get = get;
        _set = set;
    }

    public T Get() => _get();

    public void Set(T value)
    {
        if (_set is null)
            throw new InvalidOperationException($"{_field.Name} is bound read-only");
        _set(value);
    }
}

/// <summary>A snapshot of an element's fields, in declaration order:
/// what a tick describes and diffs, and what a test asserts against.</summary>
public sealed class FieldSet
{
    private readonly List<KeyValuePair<Field, object?>> _entries = new();
    private readonly Dictionary<Field, int> _index = new(ReferenceEqualityComparer.Instance);

    public int Count => _entries.Count;

    /// <summary>The entries, in the order they were described.</summary>
    public IReadOnlyList<KeyValuePair<Field, object?>> Entries => _entries;

    public void Set<T>(Field<T> field, T value)
    {
        if (_index.TryGetValue(field, out var at))
            _entries[at] = new KeyValuePair<Field, object?>(field, value);
        else
        {
            _index[field] = _entries.Count;
            _entries.Add(new KeyValuePair<Field, object?>(field, value));
        }
    }

    public bool Contains(Field field) => _index.ContainsKey(field);

    /// <summary>Drop a field from the description — for a subclass whose
    /// override of <see cref="Element.DescribeFields"/> withholds one
    /// of its base's fields in some state. True when it was present.</summary>
    public bool Remove(Field field)
    {
        if (!_index.TryGetValue(field, out var at))
            return false;
        _entries.RemoveAt(at);
        _index.Remove(field);
        for (var i = at; i < _entries.Count; i++)
            _index[_entries[i].Key] = i;
        return true;
    }

    public bool TryGet<T>(Field<T> field, out T value)
    {
        if (_index.TryGetValue(field, out var at))
        {
            value = (T)_entries[at].Value!;
            return true;
        }
        value = default!;
        return false;
    }

    /// <summary>The field's value, or default when absent.</summary>
    public T? Get<T>(Field<T> field) => TryGet(field, out var value) ? value : default;

    internal bool TryGetBoxed(Field field, out object? value)
    {
        if (_index.TryGetValue(field, out var at))
        {
            value = _entries[at].Value;
            return true;
        }
        value = null;
        return false;
    }
}

/// <summary>Anything that describes itself through fields: widgets,
/// and the items a list or tree holds. A subclass declares its fields
/// as [<see cref="FieldAttribute">Field</see>] properties and the
/// generator supplies the rest — <see cref="DescribeFields"/>,
/// <see cref="TryGet{T}"/>, <see cref="TrySet{T}"/>, and the
/// implementation of partial ones. Any field can be bound to the
/// program's model (<see cref="Bind{T}"/>): reads then come from the
/// getter and a widget's writes go to the setter, so the program never
/// synchronizes anything — the field writes itself.</summary>
public abstract class Element
{
    private Dictionary<Field, object>? _bindings;

    /// <summary>Connect a field to the program's model. Reads come from
    /// <paramref name="get"/>; writes (a widget's input, or an
    /// assignment to the property) go to <paramref name="set"/>, or
    /// throw when none is given. Getters must be cheap and pure: the
    /// focused widget's fields are read at every tick end.</summary>
    public void Bind<T>(Field<T> field, Func<T> get, Action<T>? set = null)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(get);
        (_bindings ??= new Dictionary<Field, object>(ReferenceEqualityComparer.Instance))[field] =
            new Binding<T>(field, get, set);
    }

    /// <summary>Remove a field's binding; the field reverts to its
    /// stored value. True when one was installed.</summary>
    public bool Unbind(Field field) => _bindings?.Remove(field) == true;

    /// <summary>Whether the field is bound.</summary>
    public bool IsBound(Field field) => _bindings?.ContainsKey(field) == true;

    /// <summary>The binding installed on a field, for generated
    /// accessors.</summary>
    protected bool TryGetBinding<T>(Field<T> field, [NotNullWhen(true)] out Binding<T>? binding)
    {
        if (_bindings is not null && _bindings.TryGetValue(field, out var boxed))
        {
            binding = (Binding<T>)boxed;
            return true;
        }
        binding = null;
        return false;
    }

    /// <summary>Write every field into the set. Generated per type;
    /// each override calls its base first, so a subclass's fields
    /// follow its ancestors'.</summary>
    public virtual void DescribeFields(FieldSet s)
    {
    }

    /// <summary>A fresh description of every field.</summary>
    public FieldSet Describe()
    {
        var s = new FieldSet();
        DescribeFields(s);
        return s;
    }

    /// <summary>Read one field by key. False when this element has no
    /// such field.</summary>
    public virtual bool TryGet<T>(Field<T> field, out T value)
    {
        value = default!;
        return false;
    }

    /// <summary>The field's value, or default when this element has no
    /// such field — the natural reading for nullable keys (an item
    /// without Checked is not checked).</summary>
    public T? Get<T>(Field<T> field) => TryGet(field, out var value) ? value : default;

    /// <summary>Write one field by key. False when this element has no
    /// such field, or it has no setter.</summary>
    public virtual bool TrySet<T>(Field<T> field, T value) => false;

    /// <summary>A field was written through its property — stored or
    /// bound. Widgets tell the engine something may have changed.</summary>
    protected virtual void OnFieldWritten(Field field)
    {
    }
}
