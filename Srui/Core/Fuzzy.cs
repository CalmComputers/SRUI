namespace Srui.Core;

/// <summary>
/// Matching and scoring for list filtering: the default behind
/// <see cref="IListItem.FilterScore"/>, and the scorer an item type
/// composes its own ranking from.
///
/// A score is a tier plus detail, and the tiers are far apart so that
/// how well the query matches always decides before anything else
/// does. From the top: the query appears contiguously at the start of
/// the target (<see cref="SubstringTier"/> + <see cref="PrefixBonus"/>),
/// contiguously at the start of a word (+ <see cref="WordStartBonus"/>),
/// contiguously anywhere (<see cref="SubstringTier"/> alone), or only
/// as a subsequence - every character in order, with other characters
/// between (<see cref="FuzzyTier"/>). Within a tier, a small detail
/// score prefers earlier positions, longer consecutive runs and word
/// starts; it never reaches <see cref="DetailLimit"/>.
///
/// An item type that ranks on more than match quality - a recently
/// used command, a result from a preferred source - adds its own
/// bonus to this score. Keep it under <see cref="BonusLimit"/>: such a
/// bonus then reorders only among matches of the same kind, above the
/// detail and below the tiers, and can never lift a scattered match
/// over a prefix one.
/// </summary>
public static class Fuzzy
{
    /// <summary>The query appears contiguously in the target.</summary>
    public const int SubstringTier = 200_000;

    /// <summary>The query's characters appear in order but not
    /// contiguously.</summary>
    public const int FuzzyTier = 100_000;

    /// <summary>Added in the substring tier when the match starts the
    /// target.</summary>
    public const int PrefixBonus = 20_000;

    /// <summary>Added in the substring tier when the match starts a
    /// word (but not the target).</summary>
    public const int WordStartBonus = 10_000;

    /// <summary>The detail score within a tier stays below this, so a
    /// consumer's bonus outranks it.</summary>
    public const int DetailLimit = 1_000;

    /// <summary>A consumer's own bonuses stay below this, so they sort
    /// within a match kind and never across kinds: a bonus and the
    /// detail together fit inside the gap between kinds.</summary>
    public const int BonusLimit = WordStartBonus - DetailLimit;

    /// <summary>Scores the target against the query, or null if the
    /// query's characters do not all appear in the target in order
    /// (case-insensitive). An empty query scores 0 against anything.
    /// See the class summary for the tiers.</summary>
    public static int? FuzzyScore(string query, string target)
    {
        if (query.Length == 0)
            return 0;

        var at = target.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (at >= 0)
        {
            var kind = at == 0 ? PrefixBonus
                : IsWordBoundary(target, at) ? WordStartBonus
                : 0;
            return SubstringTier + kind + Math.Max(0, 100 - at);
        }

        return SubsequenceScore(query, target) is { } detail ? FuzzyTier + detail : null;
    }

    /// <summary>Subsequence detail: two-pass matching (forward greedy
    /// and backward greedy), taking the better alignment, which catches
    /// one later in the string that forward-only matching would miss.
    /// Per matched position: +10 consecutive, +8 at a word boundary,
    /// +5 for the first query character at position 0, -1 per skipped
    /// character between matches. Null if not a subsequence.</summary>
    private static int? SubsequenceScore(string query, string target)
    {
        var targetChars = target.ToCharArray();
        var targetLower = new char[targetChars.Length];
        for (var i = 0; i < targetChars.Length; i++)
            targetLower[i] = char.ToLowerInvariant(targetChars[i]);
        var queryLower = new char[query.Length];
        for (var i = 0; i < query.Length; i++)
            queryLower[i] = char.ToLowerInvariant(query[i]);

        var forward = GreedyForward(queryLower, targetChars, targetLower);
        var backward = GreedyBackward(queryLower, targetChars, targetLower);

        return (forward, backward) switch
        {
            (int f, int b) => Math.Max(f, b),
            (int f, null) => f,
            (null, int b) => b,
            _ => null,
        };
    }

    /// <summary>Forward greedy: scan left-to-right, grab the first
    /// matching character.</summary>
    private static int? GreedyForward(char[] query, char[] targetChars, char[] targetLower)
    {
        var positions = new int[query.Length];
        var count = 0;
        var t = 0;
        foreach (var qc in query)
        {
            var found = false;
            while (t < targetLower.Length)
            {
                if (targetLower[t] == qc)
                {
                    positions[count++] = t;
                    t++;
                    found = true;
                    break;
                }
                t++;
            }
            if (!found)
                return null;
        }
        return ScorePositions(positions, targetChars);
    }

    /// <summary>Backward greedy: scan right-to-left with the reversed
    /// query, grabbing the last matching character.</summary>
    private static int? GreedyBackward(char[] query, char[] targetChars, char[] targetLower)
    {
        var positions = new int[query.Length];
        var count = query.Length;
        var t = targetLower.Length;
        for (var qi = query.Length - 1; qi >= 0; qi--)
        {
            var qc = query[qi];
            while (true)
            {
                if (t == 0)
                    return null;
                t--;
                if (targetLower[t] == qc)
                {
                    positions[--count] = t;
                    break;
                }
            }
        }
        return ScorePositions(positions, targetChars);
    }

    private static int ScorePositions(int[] positions, char[] targetChars)
    {
        var score = 0;
        for (var qi = 0; qi < positions.Length; qi++)
        {
            var pos = positions[qi];
            if (qi > 0 && pos == positions[qi - 1] + 1)
                score += 10;
            if (IsWordBoundary(targetChars, pos))
                score += 8;
            if (qi == 0 && pos == 0)
                score += 5;
            if (qi > 0)
                score -= pos - positions[qi - 1] - 1;
        }
        return score;
    }

    private static bool IsWordBoundary(string text, int pos) =>
        pos == 0 || IsBoundaryBefore(text[pos - 1], text[pos]);

    private static bool IsWordBoundary(char[] chars, int pos) =>
        pos == 0 || IsBoundaryBefore(chars[pos - 1], chars[pos]);

    private static bool IsBoundaryBefore(char prev, char current)
    {
        if (prev is ' ' or '_' or '-' or '.' or '/' or '\\' or ':')
            return true;
        // camelCase: previous is lowercase, current is uppercase.
        return char.IsLower(prev) && char.IsUpper(current);
    }

    /// <summary>True if all characters of the query appear in the target
    /// in order, case-insensitive. An empty query matches everything.</summary>
    public static bool FuzzyMatch(string query, string target) =>
        FuzzyScore(query, target) is not null;

    /// <summary>Score the items against the query and return the matching
    /// ones sorted by descending score — ties broken by shorter target
    /// first (the query explains more of it: "find" puts "Find" above
    /// "Find Next"), then ordinal order. An empty query returns all
    /// items in their original order.</summary>
    public static List<string> FilterItems(string query, IReadOnlyList<string> items)
    {
        if (query.Length == 0)
            return new List<string>(items);
        var scored = new List<(int Score, string Item)>(items.Count);
        foreach (var item in items)
            if (FuzzyScore(query, item) is int score)
                scored.Add((score, item));
        scored.Sort(static (a, b) =>
        {
            var byScore = b.Score.CompareTo(a.Score);
            if (byScore != 0)
                return byScore;
            var byLength = a.Item.Length.CompareTo(b.Item.Length);
            return byLength != 0 ? byLength : string.CompareOrdinal(a.Item, b.Item);
        });
        var result = new List<string>(scored.Count);
        foreach (var (_, item) in scored)
            result.Add(item);
        return result;
    }

    /// <summary>Score the items against the query through each item's own
    /// <see cref="IListItem.FilterScore"/> (null excludes) and return the
    /// matching ones sorted by descending score — ties broken by
    /// shorter Text first, then ordinal Text order. An empty query
    /// returns all items in their original order without consulting
    /// scores.</summary>
    public static List<T> FilterItems<T>(string query, IReadOnlyList<T> items)
        where T : IListItem
    {
        if (query.Length == 0)
            return new List<T>(items);
        var scored = new List<(int Score, T Item)>(items.Count);
        foreach (var item in items)
            if (item.FilterScore(query) is int score)
                scored.Add((score, item));
        scored.Sort(static (a, b) =>
        {
            var byScore = b.Score.CompareTo(a.Score);
            if (byScore != 0)
                return byScore;
            var byLength = a.Item.Text.Length.CompareTo(b.Item.Text.Length);
            return byLength != 0 ? byLength : string.CompareOrdinal(a.Item.Text, b.Item.Text);
        });
        var result = new List<T>(scored.Count);
        foreach (var (_, item) in scored)
            result.Add(item);
        return result;
    }
}
