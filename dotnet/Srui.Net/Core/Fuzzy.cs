namespace Srui.Core;

/// <summary>Fuzzy matching and scoring for list filtering.</summary>
internal static class Fuzzy
{
    /// <summary>Returns a score if all characters of the query appear in
    /// the target in order (case-insensitive); null if the query doesn't
    /// match. An empty query always scores 0. Uses two-pass matching
    /// (forward greedy + backward greedy) and takes the better score,
    /// catching alignments later in the string that forward-only matching
    /// would miss.
    ///
    /// Scoring per match position: +10 consecutive match, +8 word-boundary
    /// match (position 0, after space/underscore/dash/dot/slash, or
    /// camelCase), +5 first query char at target position 0, -1 per gap
    /// character between matches.</summary>
    public static int? FuzzyScore(string query, string target)
    {
        if (query.Length == 0)
            return 0;

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

    private static bool IsWordBoundary(char[] chars, int pos)
    {
        if (pos == 0)
            return true;
        var prev = chars[pos - 1];
        if (prev is ' ' or '_' or '-' or '.' or '/')
            return true;
        // camelCase: previous is lowercase, current is uppercase.
        return char.IsLower(prev) && char.IsUpper(chars[pos]);
    }

    /// <summary>True if all characters of the query appear in the target
    /// in order, case-insensitive. An empty query matches everything.</summary>
    public static bool FuzzyMatch(string query, string target) =>
        FuzzyScore(query, target) is not null;

    /// <summary>Score the items against the query and return the matching
    /// ones sorted by descending score, ties broken by ordinal order. An
    /// empty query returns all items in their original order.</summary>
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
            return byScore != 0 ? byScore : string.CompareOrdinal(a.Item, b.Item);
        });
        var result = new List<string>(scored.Count);
        foreach (var (_, item) in scored)
            result.Add(item);
        return result;
    }
}
