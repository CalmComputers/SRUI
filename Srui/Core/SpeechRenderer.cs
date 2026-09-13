using System.Text;

namespace Srui;

/// <summary>What typing speaks as it lands. Gates insertion only:
/// deletions always speak what they removed — that is confirmation of
/// a destruction, not typing chatter.</summary>
public enum TypingEcho
{
    /// <summary>Every character, and the word it completed on a
    /// separator: "hello space". The default.</summary>
    Both,

    /// <summary>Every character; no completed words.</summary>
    Characters,

    /// <summary>Only the completed word, on the separator or Enter
    /// that finished it.</summary>
    Words,

    /// <summary>Nothing.</summary>
    None,
}

/// <summary>What the widget under a closed dialog announces when focus
/// returns to it. The user never left the widget, so this is
/// re-orientation rather than news, and how much of it to speak is an
/// experience curve: the full announcement re-anchors a user who lost
/// the thread while the dialog was up, the delta serves one who trusts
/// that silence means nothing changed, and nothing serves the expert
/// whose hands already know where they are. Whatever the mode, a
/// result the dialog announced before closing still speaks, and a
/// restore whose widget is gone reads in full as the recovery it
/// is.</summary>
public enum RestoreAnnouncement
{
    /// <summary>The normal focus announcement, under the same
    /// verbosity as any other focus. The default.</summary>
    Full,

    /// <summary>Only what changed while the dialog was open — value
    /// and state; an unchanged widget restores in silence.</summary>
    Changes,

    /// <summary>Nothing.</summary>
    None,
}

/// <summary>What the reference rendering speaks beyond the essentials.
/// Everything on is the default. A reader holds one instance
/// (<see cref="SpeechReader.Verbosity"/>) and the app mutates its
/// fields live; the renderer consults it per tick. Name, value, and
/// the actionable states (unavailable, required, warning) are never
/// suppressible — those are information, not verbosity.
/// <see cref="Restore"/> is the one whole-utterance switch, and it may
/// trim information: the return from a closed dialog is a
/// re-orientation the user can opt out of wholesale, not a fact they
/// never chose to hear.</summary>
public sealed class SpeechVerbosity
{
    /// <summary>Speak widget roles ("button", "slider").</summary>
    public bool Roles = true;

    /// <summary>Speak the widget's shortcut suffix ("alt h").</summary>
    public bool Shortcuts = true;

    /// <summary>Speak descriptions and the "with help" hint.</summary>
    public bool Extras = true;

    /// <summary>What typing speaks as it lands.</summary>
    public TypingEcho Echo = TypingEcho.Both;

    /// <summary>What the widget under a closed dialog announces when
    /// focus returns to it. Consulted by the engine at the tick end:
    /// under <see cref="RestoreAnnouncement.Changes"/> an unchanged
    /// widget produces no state at all, and under
    /// <see cref="RestoreAnnouncement.None"/> none ever does.</summary>
    public RestoreAnnouncement Restore = RestoreAnnouncement.Full;
}

/// <summary>What a field renderer sees besides the value: the widget
/// the readout describes, whether the readout is a focus or item
/// arrival (a full reading, where a false state says nothing) or a
/// delta (where it says "not checked"), the verbosity, and the other
/// fields of the readout — or, for one the tick did not carry, the
/// widget's live value.</summary>
public sealed class ReadoutContext
{
    internal ReadoutContext(Widget widget, SpeechVerbosity verbosity, bool arrival, IReadOnlyList<AccessibilityEvent.FieldValue> fields)
    {
        Widget = widget;
        Verbosity = verbosity;
        IsArrival = arrival;
        _fields = fields;
    }

    private readonly IReadOnlyList<AccessibilityEvent.FieldValue> _fields;

    public Widget Widget { get; }

    public SpeechVerbosity Verbosity { get; }

    /// <summary>Whether the readout reads the widget (or its landed
    /// item) in full, as against a delta.</summary>
    public bool IsArrival { get; }

    /// <summary>A field's value: from this readout when the tick
    /// carried it, else read live from the widget or the item under its
    /// cursor.</summary>
    public T? Get<T>(Field<T> field)
    {
        foreach (var f in _fields)
            if (ReferenceEquals(f.Field, field))
                return f.Value is null ? default : (T)f.Value;
        if (Widget.TryGet(field, out var own))
            return own;
        if (Widget.CurrentItem is { } item && item.TryGet(field, out var fromItem))
            return fromItem;
        return default;
    }
}

/// <summary>Speech rendering — the reference rendering of a tick's
/// events to utterances. A tick's announcements and action events each
/// become one utterance, in order; its state (a focus arrival, an item
/// arrival, and the field values) becomes one utterance composed from
/// the fields in this renderer's order, each field through its own
/// small rendering function. The self-voicing <see cref="SpeechReader"/>
/// uses the shared <see cref="Default"/>; a program registers its own
/// fields' renderings on it (<see cref="Register{T}"/>), and its own
/// roles' words (<see cref="SetRoleName"/>). Braille and platform
/// readers ignore all of this and read the structured payloads
/// directly; tests assert against the rendering.</summary>
public sealed class SpeechRenderer
{
    private static readonly SpeechVerbosity Full = new();

    /// <summary>Maximum characters to announce verbatim for selections;
    /// beyond this we say "N characters selected".</summary>
    public const int SpeakLimit = 500;

    /// <summary>The renderer the speech reader and the test harness
    /// share. Registrations on it reach both.</summary>
    public static SpeechRenderer Default { get; } = new();

    private delegate string? FieldRenderer(ReadoutContext ctx, object? value);

    // Copy-on-write: a registration replaces the tables outright, so a
    // tick rendering on another thread (a test suite running classes
    // in parallel against the shared Default) never sees a table
    // change under it.
    private List<Field> _order = new();
    private Dictionary<Field, FieldRenderer> _renderers = new(ReferenceEqualityComparer.Instance);
    private Dictionary<Role, string> _roleNames = new(ReferenceEqualityComparer.Instance);

    public SpeechRenderer()
    {
        // NVDA order: name, role, value, states, description, shortcut —
        // with the fields that qualify a role beside it, and the item's
        // own fields around the value.
        Register(Fields.Name, static (_, v) => v);
        Register(Fields.MultiSelect, static (_, v) => v ? "multi select" : null);
        Register(Fields.Role, (ctx, v) => ctx.Verbosity.Roles ? RoleName(v) : null);
        // The shared tables are read without a lock: a rendering holds
        // the references it started with.
        Register(Fields.ReadOnly, static (ctx, v) => v ? "read only" : ctx.IsArrival ? null : "editable");
        Register(Fields.Multiline, static (_, v) => v ? "multi line" : null);
        Register(Fields.Value, static (ctx, v) =>
        {
            if (ctx.Get(Fields.SelectedText) is not null)
                return null;
            if (v is null)
                return ReferenceEquals(ctx.Widget.Role, Role.ShortcutField) ? "blank" : null;
            return v.Length == 0 ? null : v;
        });
        Register(Fields.SelectedText, static (ctx, v) =>
            v is null ? (ctx.IsArrival ? null : "Selection removed") : $"selected {v}");
        Register(Fields.Expanded, static (ctx, v) =>
            ctx.Get(Fields.ChildCount) > 0 ? (v ? "expanded" : "collapsed") : null);
        Register(Fields.ChildCount, static (_, v) => v > 0 ? $"{v} items" : null);
        Register(Fields.Checked, static (ctx, v) => v switch
        {
            true => "checked",
            false when ReferenceEquals(ctx.Widget.Role, Role.CheckBox) || !ctx.IsArrival => "not checked",
            _ => null,
        });
        Register(Fields.Number, static (ctx, v) => $"{v}{ctx.Get(Fields.Unit)}");
        Register(Fields.Unit, static (_, _) => null);
        Register(Fields.Min, static (_, _) => null);
        Register(Fields.Max, static (_, _) => null);
        Register(Fields.Position, static (_, v) => v is { } p ? $"{p.Index + 1} of {p.Total}" : null);
        Register(Fields.Password, static (_, v) => v ? "protected" : null);
        Register(Fields.Filter, static (_, v) => v is null ? "no filter" : $"filter {v}");
        Register(Fields.Count, static (ctx, v) =>
            v == 0 ? (ReferenceEquals(ctx.Widget.Role, Role.FilterList) ? "no results" : "empty") : null);
        Register(Fields.Level, static (_, _) => null);
        Register(Fields.Disabled, static (ctx, v) => v ? "unavailable" : ctx.IsArrival ? null : "available");
        Register(Fields.Required, static (ctx, v) => v ? "required" : ctx.IsArrival ? null : "not required");
        Register(Fields.Warning, static (ctx, v) => v ? "warning" : ctx.IsArrival ? null : "warning cleared");
        Register(Fields.WithHelp, static (ctx, v) =>
            !ctx.Verbosity.Extras ? null : v ? "with help" : ctx.IsArrival ? null : "help removed");
        Register(Fields.Description, static (ctx, v) =>
            ctx.Verbosity.Extras && v.Length != 0 ? v : null);
        Register(Fields.Shortcuts, static (ctx, v) =>
            ctx.Verbosity.Shortcuts && v.Count != 0 ? v[0].DisplayName() : null);

        SetRoleName(Role.FilterList, "list");
    }

    /// <summary>Install (or replace) the rendering of a field: given the
    /// context and the value, the words, or null for nothing. New
    /// fields join the order after <paramref name="after"/>, or at the
    /// end. A field with no registration speaks its value when that is
    /// a string, and nothing otherwise.</summary>
    public void Register<T>(Field<T> field, Func<ReadoutContext, T, string?> render, Field? after = null)
    {
        lock (_registration)
        {
            var renderers = new Dictionary<Field, FieldRenderer>(_renderers, ReferenceEqualityComparer.Instance)
            {
                [field] = (ctx, v) => render(ctx, v is null ? default! : (T)v),
            };
            var order = new List<Field>(_order);
            if (!order.Contains(field))
            {
                var at = after is null ? -1 : order.IndexOf(after);
                if (at < 0)
                    order.Add(field);
                else
                    order.Insert(at + 1, field);
            }
            _renderers = renderers;
            _order = order;
        }
    }

    private readonly object _registration = new();

    /// <summary>What a role is called; unset roles speak their name.</summary>
    public void SetRoleName(Role role, string name)
    {
        lock (_registration)
            _roleNames = new Dictionary<Role, string>(_roleNames, ReferenceEqualityComparer.Instance) { [role] = name };
    }

    private string? RoleName(Role role)
    {
        var name = _roleNames.TryGetValue(role, out var custom) ? custom : role.Name;
        return name.Length == 0 ? null : name;
    }

    // ── A tick ──

    /// <summary>Render a tick's events to utterances: announcements and
    /// action events one each, in order, and the state as one readout
    /// composed from its fields.</summary>
    public List<string> RenderTick(IReadOnlyList<AccessibilityEvent> events, SpeechVerbosity? verbosity = null)
    {
        verbosity ??= Full;
        var result = new List<string>();
        AccessibilityEvent.FocusArrived? focus = null;
        AccessibilityEvent.ItemArrived? item = null;
        Boundary? boundary = null;
        List<AccessibilityEvent.FieldValue>? fields = null;
        Widget? widget = null;

        void Flush()
        {
            if (widget is null)
                return;
            var text = RenderReadout(widget, focus, item, boundary, fields ?? [], verbosity);
            if (text is not null)
                result.Add(text);
            focus = null;
            item = null;
            boundary = null;
            fields = null;
            widget = null;
        }

        foreach (var e in events)
        {
            switch (e)
            {
                case AccessibilityEvent.FocusArrived f:
                    Flush();
                    widget = f.Widget;
                    focus = f;
                    break;
                case AccessibilityEvent.ItemArrived i:
                    widget ??= i.Widget;
                    item = i;
                    break;
                case AccessibilityEvent.BoundaryHit b:
                    widget ??= b.Widget;
                    boundary = b.Edge;
                    break;
                case AccessibilityEvent.FieldValue v:
                    widget ??= v.Widget;
                    (fields ??= []).Add(v);
                    break;
                default:
                    Flush();
                    if (RenderAction(e, verbosity) is { } text)
                        result.Add(text);
                    break;
            }
        }
        Flush();
        return result;
    }

    /// <summary>One readout: context, the edge, then the fields in the
    /// renderer's order.</summary>
    private string? RenderReadout(
        Widget widget, AccessibilityEvent.FocusArrived? focus, AccessibilityEvent.ItemArrived? item,
        Boundary? boundary, List<AccessibilityEvent.FieldValue> fields, SpeechVerbosity verbosity)
    {
        var sb = new StringBuilder(64);
        void Append(string? part)
        {
            if (string.IsNullOrEmpty(part))
                return;
            if (sb.Length != 0)
                sb.Append(' ');
            sb.Append(part);
        }

        if (focus is not null)
            foreach (var entry in focus.Context)
            {
                if (ReferenceEquals(entry.Role, Role.Label))
                    Append(entry.Name);
                else if (entry.Name is null)
                    Append(RoleName(entry.Role));
                else
                    Append($"{entry.Name} {RoleName(entry.Role)}".TrimEnd());
            }
        Append(boundary switch
        {
            Boundary.Top => "top,",
            Boundary.Bottom => "bottom,",
            Boundary.Left => "left,",
            Boundary.Right => "right,",
            _ => null,
        });

        var ctx = new ReadoutContext(widget, verbosity, focus is not null || item is not null, fields);
        var order = _order;
        var renderers = _renderers;
        foreach (var field in order)
        {
            foreach (var f in fields)
            {
                if (!ReferenceEquals(f.Field, field))
                    continue;
                Append(renderers[field](ctx, f.Value));
            }
        }
        // A field with no rendering registered: a string speaks as it
        // is (a program's composed line), anything else is silent until
        // the program says how it sounds.
        foreach (var f in fields)
        {
            if (renderers.ContainsKey(f.Field))
                continue;
            if (f.Value is string text)
                Append(text);
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>Render one announcement or action event to an
    /// utterance. Null when there is nothing to say.</summary>
    public string? RenderAction(AccessibilityEvent ev, SpeechVerbosity? verbosity = null)
    {
        verbosity ??= Full;
        switch (ev)
        {
            case AccessibilityEvent.Typing(_, var grapheme, var lastWord, var kind):
            {
                // On a word boundary the just-completed word is echoed
                // before the separator: "hello space". Echo gates
                // insertion only; deletions always speak. For deletes,
                // grapheme is already in spoken form (SpeakChar passes
                // multi-char strings through unchanged).
                var chars = verbosity.Echo is TypingEcho.Both or TypingEcho.Characters;
                var words = verbosity.Echo is TypingEcho.Both or TypingEcho.Words;
                return kind switch
                {
                    TypingKind.Insert when words && lastWord is not null && chars =>
                        $"{lastWord} {SpeakChar(grapheme)}",
                    TypingKind.Insert when words && lastWord is not null => lastWord,
                    TypingKind.Insert when chars && grapheme.Length != 0 => SpeakChar(grapheme),
                    TypingKind.Insert => null,
                    TypingKind.Delete when grapheme.Length != 0 => SpeakChar(grapheme),
                    TypingKind.Delete => null,
                    TypingKind.DeleteWord => lastWord,
                    _ => null,
                };
            }

            case AccessibilityEvent.TextNav(_, _, var context, _, var boundary):
                return boundary switch
                {
                    Boundary.Top => $"Top, {context}",
                    Boundary.Bottom => $"Bottom, {context}",
                    _ => context,
                };

            case AccessibilityEvent.Selection(_, var delta, var kind):
                return kind switch
                {
                    SelectionKind.Selected or SelectionKind.All => $"{delta} selected",
                    SelectionKind.Unselected => $"{delta} unselected",
                    _ => "Selection removed",
                };

            case AccessibilityEvent.Clipboard(_, var op):
                return op switch
                {
                    ClipboardOp.Copy => "Copy",
                    ClipboardOp.Cut => "Cut",
                    _ => "Paste",
                };

            case AccessibilityEvent.EditNoop(_, var kind, var context):
                return kind switch
                {
                    EditNoopKind.NoText => "No text",
                    EditNoopKind.NothingToSelect => "Nothing to select",
                    EditNoopKind.NothingToDelete => "Nothing to delete",
                    EditNoopKind.NothingToUndo => "Nothing to undo",
                    EditNoopKind.NothingToRedo => "Nothing to redo",
                    EditNoopKind.SelectedToTop => $"Already selected to top, {context}",
                    _ => $"Already selected to bottom, {context}",
                };

            // Where the user now stands — the selection that came back,
            // or the line as it now reads.
            case AccessibilityEvent.UndoRedo(_, var context, var selected, _):
                return selected ? $"selected {context}" : context;

            case AccessibilityEvent.Announce(var text, _):
                return text;

            default:
                return null;
        }
    }

    /// <summary>Speak a character with punctuation expansion and case
    /// handling: "a" → "a", "A" → "cap A", " " → "space", "." → "dot",
    /// "\n" → "new line". Multi-char graphemes pass through unchanged.</summary>
    public static string SpeakChar(string ch) =>
        ch.Length == 1 ? SpeakSingleChar(ch[0]) : ch;

    private static string SpeakSingleChar(char c) => c switch
    {
        ' ' => "space",
        '\n' => "new line",
        '\t' => "tab",
        '\r' => "return",

        '.' => "dot",
        ',' => "comma",
        ';' => "semicolon",
        ':' => "colon",
        '!' => "bang",
        '?' => "question",
        '\'' => "tick",
        '"' => "quote",
        '(' => "left paren",
        ')' => "right paren",
        '[' => "left bracket",
        ']' => "right bracket",
        '{' => "left brace",
        '}' => "right brace",
        '<' => "less",
        '>' => "greater",
        '/' => "slash",
        '\\' => "backslash",
        '|' => "pipe",
        '@' => "at",
        '#' => "number",
        '$' => "dollar",
        '%' => "percent",
        '^' => "caret",
        '&' => "and",
        '*' => "star",
        '-' => "dash",
        '_' => "line",
        '+' => "plus",
        '=' => "equals",
        '~' => "tilde",
        '`' => "graav",

        >= 'A' and <= 'Z' => $"cap {c}",

        _ => c.ToString(),
    };
}
