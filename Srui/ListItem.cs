namespace Srui;

/// <summary>An item a <see cref="ListBox"/> or <see cref="FilterListBox"/>
/// holds. Text is the item's line — what readers speak, what typeahead
/// matches, what filtering searches. Application item types implement
/// this directly (a record composing its line from its own state);
/// plain strings ride in through <see cref="ListItem"/> and the widgets'
/// string-list convenience overloads.</summary>
public interface IListItem
{
    /// <summary>The item's line: spoken by readers, matched by typeahead,
    /// searched by filters. Read live whenever the framework needs the
    /// line (including at announcement time), so state-bearing items
    /// need no sync call after mutation — keep the getter cheap and
    /// side-effect-free.</summary>
    string Text { get; }

    /// <summary>Score this item against a filter query: null filters the
    /// item out, higher sorts first (ties fall back to ordinal Text
    /// order). The default is the built-in fuzzy match over Text —
    /// query characters in order, with word-boundary and consecutivity
    /// bonuses. Widgets do not consult scores for an empty query (all
    /// items show, list order). Override for command-palette-style
    /// ranking, or with a constant to opt out of filtering.</summary>
    int? FilterScore(string query) => Core.Fuzzy.FuzzyScore(query, Text);
}

/// <summary>A plain-text item — the wrapper the string convenience
/// overloads use. Implicit from string, so string arguments convert at
/// the call site wherever an <see cref="IListItem"/> is expected.</summary>
public sealed record ListItem(string Text) : IListItem
{
    public static implicit operator ListItem(string text) => new(text);

    public override string ToString() => Text;
}
