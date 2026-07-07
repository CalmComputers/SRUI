//! Fuzzy matching and scoring for list filtering.

/// Returns a score if all characters of `query` appear in `target` in order
/// (case-insensitive). Higher scores mean better matches. Returns `None` if
/// the query doesn't match at all. Empty query always returns `Some(0)`.
///
/// Uses two-pass matching (forward greedy + backward greedy) and takes the
/// better score. This catches cases where a better alignment exists later
/// in the string that forward-only matching would miss.
///
/// Scoring per match position:
/// - +10 consecutive match (position = previous + 1)
/// - +8  word boundary match (pos 0, or after ` `/`_`/`-`/`.`/`/`, or camelCase)
/// - +5  first query char at target position 0
/// - -1  per gap character between matches
pub fn fuzzy_score(query: &str, target: &str) -> Option<i32> {
    if query.is_empty() {
        return Some(0);
    }

    let target_chars: Vec<char> = target.chars().collect();
    let target_lower: Vec<char> = target_chars.iter().flat_map(|c| c.to_lowercase()).collect();
    let query_lower: Vec<char> = query.chars().flat_map(|c| c.to_lowercase()).collect();

    let forward = greedy_forward(&query_lower, &target_chars, &target_lower);
    let backward = greedy_backward(&query_lower, &target_chars, &target_lower);

    match (forward, backward) {
        (Some(f), Some(b)) => Some(f.max(b)),
        (Some(f), None) | (None, Some(f)) => Some(f),
        (None, None) => None,
    }
}

/// Forward greedy: scan left-to-right, grab first matching char.
fn greedy_forward(query: &[char], target_chars: &[char], target_lower: &[char]) -> Option<i32> {
    let mut positions = Vec::with_capacity(query.len());
    let mut t_idx = 0;
    for &qc in query {
        let mut found = false;
        while t_idx < target_lower.len() {
            if target_lower[t_idx] == qc {
                positions.push(t_idx);
                t_idx += 1;
                found = true;
                break;
            }
            t_idx += 1;
        }
        if !found {
            return None;
        }
    }
    Some(score_positions(&positions, target_chars))
}

/// Backward greedy: scan right-to-left with reversed query, grab last matching char.
/// Catches better alignments later in the string that forward pass misses.
fn greedy_backward(query: &[char], target_chars: &[char], target_lower: &[char]) -> Option<i32> {
    let mut positions = Vec::with_capacity(query.len());
    let mut t_idx = target_lower.len();
    for &qc in query.iter().rev() {
        loop {
            if t_idx == 0 {
                return None;
            }
            t_idx -= 1;
            if target_lower[t_idx] == qc {
                positions.push(t_idx);
                break;
            }
        }
    }
    positions.reverse();
    Some(score_positions(&positions, target_chars))
}

/// Score a set of matched positions against the target.
fn score_positions(positions: &[usize], target_chars: &[char]) -> i32 {
    let mut score: i32 = 0;
    for (qi, &pos) in positions.iter().enumerate() {
        if qi > 0 && pos == positions[qi - 1] + 1 {
            score += 10;
        }
        if is_word_boundary(target_chars, pos) {
            score += 8;
        }
        if qi == 0 && pos == 0 {
            score += 5;
        }
        if qi > 0 {
            let gap = pos as i32 - positions[qi - 1] as i32 - 1;
            score -= gap;
        }
    }
    score
}

/// Check if a position in the target is a word boundary.
fn is_word_boundary(chars: &[char], pos: usize) -> bool {
    if pos == 0 {
        return true;
    }
    let prev = chars[pos - 1];
    if matches!(prev, ' ' | '_' | '-' | '.' | '/') {
        return true;
    }
    // camelCase: previous is lowercase, current is uppercase
    let curr = chars[pos];
    prev.is_lowercase() && curr.is_uppercase()
}

/// Returns true if all characters of `query` appear in `target` in order,
/// case-insensitive. An empty query matches everything.
pub fn fuzzy_match(query: &str, target: &str) -> bool {
    fuzzy_score(query, target).is_some()
}

/// Score `items` against `query` and return the matching ones sorted by
/// descending score, ties broken alphabetically. An empty query returns
/// all items in their original order.
pub fn filter_items(query: &str, items: &[String]) -> Vec<String> {
    if query.is_empty() {
        return items.to_vec();
    }
    let mut scored: Vec<(i32, &str)> = items
        .iter()
        .filter_map(|item| fuzzy_score(query, item).map(|score| (score, item.as_str())))
        .collect();
    scored.sort_by(|a, b| b.0.cmp(&a.0).then_with(|| a.1.cmp(b.1)));
    scored.into_iter().map(|(_, s)| s.to_string()).collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn empty_query_matches_everything() {
        assert!(fuzzy_match("", "anything"));
        assert!(fuzzy_match("", ""));
        assert_eq!(fuzzy_score("", "anything"), Some(0));
    }

    #[test]
    fn exact_match() {
        assert!(fuzzy_match("hello", "hello"));
    }

    #[test]
    fn subsequence_match() {
        assert!(fuzzy_match("hlo", "hello"));
        assert!(fuzzy_match("ed", "Open Editor"));
    }

    #[test]
    fn case_insensitive() {
        assert!(fuzzy_match("HLO", "hello"));
        assert!(fuzzy_match("hello", "HELLO"));
    }

    #[test]
    fn non_match() {
        assert!(!fuzzy_match("xyz", "hello"));
        assert_eq!(fuzzy_score("xyz", "hello"), None);
    }

    #[test]
    fn order_matters() {
        assert!(!fuzzy_match("ba", "abc"));
        assert!(fuzzy_match("ab", "abc"));
    }

    #[test]
    fn query_longer_than_target() {
        assert!(!fuzzy_match("abcdef", "abc"));
    }

    #[test]
    fn prefix_match() {
        assert!(fuzzy_match("hel", "hello world"));
    }

    /// Rank candidates by score for a query, best first.
    fn rank<'a>(query: &str, candidates: &[&'a str]) -> Vec<&'a str> {
        let mut scored: Vec<(i32, &'a str)> = candidates
            .iter()
            .filter_map(|c| fuzzy_score(query, c).map(|s| (s, *c)))
            .collect();
        scored.sort_by(|a, b| b.0.cmp(&a.0).then_with(|| a.1.cmp(b.1)));
        scored.into_iter().map(|(_, c)| c).collect()
    }

    #[test]
    fn exact_match_scores_highest() {
        let r = rank("save", &["Save File", "Save", "Autosave"]);
        assert_eq!(r[0], "Save", "exact match wins: {r:?}");
    }

    #[test]
    fn prefix_beats_midstring() {
        let r = rank("save", &["Autosave File", "Save File"]);
        assert_eq!(r[0], "Save File", "prefix wins over mid-string: {r:?}");
    }

    #[test]
    fn word_initials_rank_high() {
        let r = rank("sf", &["Selfless", "Save File"]);
        assert_eq!(r[0], "Save File", "word initials win: {r:?}");
    }

    #[test]
    fn word_initials_beat_scattered_mid() {
        let r = rank("oe", &["Somebody Else", "Open Editor"]);
        assert_eq!(r[0], "Open Editor", "word initials beat scattered: {r:?}");
    }

    #[test]
    fn consecutive_beats_gaps() {
        let r = rank("open", &["Orphan Pen", "Open"]);
        assert_eq!(r[0], "Open", "consecutive run wins: {r:?}");
    }

    #[test]
    fn filter_items_empty_query_keeps_order() {
        let items = vec!["b".to_string(), "a".to_string()];
        assert_eq!(filter_items("", &items), items);
    }

    #[test]
    fn filter_items_sorts_and_drops() {
        let items = vec![
            "Autosave File".to_string(),
            "Save File".to_string(),
            "Quit".to_string(),
        ];
        let filtered = filter_items("save", &items);
        assert_eq!(filtered[0], "Save File");
        assert_eq!(filtered.len(), 2);
    }
}
