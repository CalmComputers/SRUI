//! Navigation algorithms — Tab, Shift+Tab, Alt+arrows, focus recovery.

use crate::tree::{NodeId, Tree};
use crate::types::{is_focusable, States};

/// Direction for tree navigation (Alt+arrows).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TreeDirection {
    Up,    // parent
    Down,  // first child
    Left,  // prev sibling (wraps)
    Right, // next sibling (wraps)
}

/// Tab forward: depth-first traversal to next focusable node.
/// Wraps around at the end.
pub fn tab_next(tree: &Tree, current: Option<NodeId>) -> Option<NodeId> {
    let all = collect_focusable_dfs(tree);
    if all.is_empty() {
        return None;
    }

    match current {
        None => Some(all[0]),
        Some(cur) => {
            let pos = all.iter().position(|&id| id == cur);
            match pos {
                Some(i) => Some(all[(i + 1) % all.len()]),
                None => Some(all[0]),
            }
        }
    }
}

/// Tab backward: depth-first traversal to previous focusable node.
/// Wraps around at the beginning.
pub fn tab_prev(tree: &Tree, current: Option<NodeId>) -> Option<NodeId> {
    let all = collect_focusable_dfs(tree);
    if all.is_empty() {
        return None;
    }

    match current {
        None => Some(*all.last().unwrap()),
        Some(cur) => {
            let pos = all.iter().position(|&id| id == cur);
            match pos {
                Some(0) => Some(*all.last().unwrap()),
                Some(i) => Some(all[i - 1]),
                None => Some(*all.last().unwrap()),
            }
        }
    }
}

/// Alt+arrow navigation within the tree structure.
///
/// - Up: go to parent
/// - Down: go to first non-hidden child
/// - Left: previous sibling (wraps)
/// - Right: next sibling (wraps)
pub fn tree_nav(tree: &Tree, current: NodeId, direction: TreeDirection) -> Option<NodeId> {
    match direction {
        TreeDirection::Up => tree.parent(current),

        TreeDirection::Down => {
            let children = tree.children(current);
            children.iter().copied().find(|&cid| {
                tree.get(cid)
                    .map(|n| !n.label.states.contains(States::HIDDEN))
                    .unwrap_or(false)
            })
        }

        TreeDirection::Left => {
            let siblings = sibling_list(tree, current);
            let pos = siblings.iter().position(|&id| id == current)?;
            if siblings.len() <= 1 {
                return None;
            }
            let prev = if pos == 0 {
                siblings.len() - 1
            } else {
                pos - 1
            };
            Some(siblings[prev])
        }

        TreeDirection::Right => {
            let siblings = sibling_list(tree, current);
            let pos = siblings.iter().position(|&id| id == current)?;
            if siblings.len() <= 1 {
                return None;
            }
            Some(siblings[(pos + 1) % siblings.len()])
        }
    }
}

/// Find the first non-hidden focusable widget whose `label.shortcut`
/// matches `ch` (case-insensitive). Depth-first from the tree roots,
/// so the match order follows insertion order. Returns `None` if no
/// widget claims the letter.
pub fn find_shortcut(tree: &Tree, ch: char) -> Option<NodeId> {
    let lower = ch.to_ascii_lowercase();
    for &id in &collect_focusable_dfs(tree) {
        if let Some(node) = tree.get(id) {
            if node.label.shortcut == Some(lower) {
                return Some(id);
            }
        }
    }
    None
}

/// Recover focus when the focused node has been removed.
/// Strategy: try tab_next from the nearest surviving ancestor, else first focusable root.
pub fn recover_focus(tree: &Tree, removed_parent: Option<NodeId>) -> Option<NodeId> {
    // Try to find the next focusable node from the removed node's parent
    if let Some(parent) = removed_parent {
        if tree.contains(parent) {
            // Try tab_next from parent context
            if let Some(next) = tab_next(tree, Some(parent)) {
                return Some(next);
            }
        }
    }

    // Fallback: first focusable node in the tree
    tab_next(tree, None)
}

/// Collect all focusable nodes in depth-first order.
fn collect_focusable_dfs(tree: &Tree) -> Vec<NodeId> {
    let mut result = Vec::new();
    for &root in tree.roots() {
        collect_focusable_recursive(tree, root, &mut result);
    }
    result
}

fn collect_focusable_recursive(tree: &Tree, id: NodeId, out: &mut Vec<NodeId>) {
    if let Some(node) = tree.get(id) {
        // Skip entire HIDDEN subtrees
        if node.label.states.contains(States::HIDDEN) {
            return;
        }
        if is_focusable(node.label.role, node.label.states) {
            out.push(id);
        }
        for &child in &node.children {
            collect_focusable_recursive(tree, child, out);
        }
    }
}

/// Get the sibling list for a node (either roots or parent's children),
/// filtering out HIDDEN siblings.
fn sibling_list(tree: &Tree, id: NodeId) -> Vec<NodeId> {
    let raw = match tree.parent(id) {
        Some(parent) => tree.children(parent).to_vec(),
        None => tree.roots().to_vec(),
    };
    raw.into_iter()
        .filter(|&sid| {
            tree.get(sid)
                .map(|n| !n.label.states.contains(States::HIDDEN))
                .unwrap_or(false)
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::types::{Role, States, WidgetLabel};

    fn make_label(name: &str, role: Role) -> WidgetLabel {
        WidgetLabel::new(name, role)
    }

    fn build_demo_tree() -> (Tree, Vec<NodeId>) {
        // Save (button), Options (group) -> [WordWrap (checkbox), Files (listbox)], Notes (editbox)
        let mut tree = Tree::new();
        let save = tree.insert(None, 0, make_label("Save", Role::Button));
        let options = tree.insert(None, 1, make_label("Options", Role::Group));
        let wrap = tree.insert(Some(options), 0, make_label("Word Wrap", Role::CheckBox));
        let files = tree.insert(Some(options), 1, make_label("Recent Files", Role::ListBox));
        let notes = tree.insert(None, 2, make_label("Notes", Role::edit()));

        (tree, vec![save, options, wrap, files, notes])
    }

    #[test]
    fn tab_next_cycles_through_focusable() {
        let (tree, ids) = build_demo_tree();
        let [save, _options, wrap, files, notes] = [ids[0], ids[1], ids[2], ids[3], ids[4]];

        // Start from None → first focusable
        assert_eq!(tab_next(&tree, None), Some(save));

        // Save → Word Wrap (skips Options group)
        assert_eq!(tab_next(&tree, Some(save)), Some(wrap));

        // Word Wrap → Recent Files
        assert_eq!(tab_next(&tree, Some(wrap)), Some(files));

        // Recent Files → Notes
        assert_eq!(tab_next(&tree, Some(files)), Some(notes));

        // Notes → Save (wraps)
        assert_eq!(tab_next(&tree, Some(notes)), Some(save));
    }

    #[test]
    fn tab_prev_cycles_reverse() {
        let (tree, ids) = build_demo_tree();
        let [save, _options, wrap, files, notes] = [ids[0], ids[1], ids[2], ids[3], ids[4]];

        assert_eq!(tab_prev(&tree, Some(save)), Some(notes));
        assert_eq!(tab_prev(&tree, Some(wrap)), Some(save));
        assert_eq!(tab_prev(&tree, Some(files)), Some(wrap));
        assert_eq!(tab_prev(&tree, Some(notes)), Some(files));
    }

    #[test]
    fn tab_empty_tree() {
        let tree = Tree::new();
        assert_eq!(tab_next(&tree, None), None);
        assert_eq!(tab_prev(&tree, None), None);
    }

    #[test]
    fn tree_nav_down_into_group() {
        let (tree, ids) = build_demo_tree();
        let [_save, options, wrap, ..] = [ids[0], ids[1], ids[2], ids[3], ids[4]];

        // Down from Options → first child (Word Wrap)
        assert_eq!(tree_nav(&tree, options, TreeDirection::Down), Some(wrap));
    }

    #[test]
    fn tree_nav_up_to_parent() {
        let (tree, ids) = build_demo_tree();
        let [_save, options, wrap, ..] = [ids[0], ids[1], ids[2], ids[3], ids[4]];

        assert_eq!(tree_nav(&tree, wrap, TreeDirection::Up), Some(options));
    }

    #[test]
    fn tree_nav_siblings_wrap() {
        let (tree, ids) = build_demo_tree();
        let [_save, _options, wrap, files, _notes] = [ids[0], ids[1], ids[2], ids[3], ids[4]];

        // Right from Word Wrap → Recent Files
        assert_eq!(tree_nav(&tree, wrap, TreeDirection::Right), Some(files));
        // Right from Recent Files → Word Wrap (wraps)
        assert_eq!(tree_nav(&tree, files, TreeDirection::Right), Some(wrap));
        // Left from Word Wrap → Recent Files (wraps)
        assert_eq!(tree_nav(&tree, wrap, TreeDirection::Left), Some(files));
    }

    #[test]
    fn tree_nav_root_siblings() {
        let (tree, ids) = build_demo_tree();
        let [save, options, _wrap, _files, notes] = [ids[0], ids[1], ids[2], ids[3], ids[4]];

        assert_eq!(tree_nav(&tree, save, TreeDirection::Right), Some(options));
        assert_eq!(tree_nav(&tree, notes, TreeDirection::Right), Some(save));
    }

    #[test]
    fn hidden_subtree_skipped_by_tab() {
        let mut tree = Tree::new();
        let a = tree.insert(None, 0, make_label("A", Role::Button));
        let g = tree.insert(None, 1, make_label("G", Role::Group));
        let b = tree.insert(Some(g), 0, make_label("B", Role::Button));
        let c = tree.insert(None, 2, make_label("C", Role::Button));

        // Mark group as HIDDEN — its children should be skipped
        tree.get_mut(g).unwrap().label.states |= States::HIDDEN;

        assert_eq!(tab_next(&tree, None), Some(a));
        assert_eq!(tab_next(&tree, Some(a)), Some(c));
        assert_eq!(tab_next(&tree, Some(c)), Some(a));
        // B is inside hidden group, never reachable
        assert_eq!(tab_next(&tree, Some(b)), Some(a));
    }

    #[test]
    fn hidden_node_skipped_by_tab() {
        let mut tree = Tree::new();
        let a = tree.insert(None, 0, make_label("A", Role::Button));
        let b = tree.insert(None, 1, make_label("B", Role::Button));
        let c = tree.insert(None, 2, make_label("C", Role::Button));

        tree.get_mut(b).unwrap().label.states |= States::HIDDEN;

        assert_eq!(tab_next(&tree, Some(a)), Some(c));
        assert_eq!(tab_next(&tree, Some(c)), Some(a));
    }

    #[test]
    fn tree_nav_down_skips_hidden_child() {
        let mut tree = Tree::new();
        let g = tree.insert(None, 0, make_label("G", Role::Group));
        let hidden = tree.insert(Some(g), 0, make_label("H", Role::Button));
        let visible = tree.insert(Some(g), 1, make_label("V", Role::Button));

        tree.get_mut(hidden).unwrap().label.states |= States::HIDDEN;

        assert_eq!(tree_nav(&tree, g, TreeDirection::Down), Some(visible));
    }

    #[test]
    fn sibling_nav_skips_hidden() {
        let mut tree = Tree::new();
        let a = tree.insert(None, 0, make_label("A", Role::Button));
        let b = tree.insert(None, 1, make_label("B", Role::Button));
        let c = tree.insert(None, 2, make_label("C", Role::Button));

        tree.get_mut(b).unwrap().label.states |= States::HIDDEN;

        assert_eq!(tree_nav(&tree, a, TreeDirection::Right), Some(c));
        assert_eq!(tree_nav(&tree, c, TreeDirection::Left), Some(a));
    }

    #[test]
    fn recover_focus_after_removal() {
        let (mut tree, ids) = build_demo_tree();
        let [save, options, wrap, files, _notes] = [ids[0], ids[1], ids[2], ids[3], ids[4]];

        tree.set_focus(wrap);
        tree.remove(wrap);

        // Recover from parent (options)
        let recovered = recover_focus(&tree, Some(options));
        // Should find files (next focusable under options) or save
        assert!(recovered.is_some());
        assert!(recovered == Some(files) || recovered == Some(save));
    }
}

#[cfg(test)]
mod proptests {
    use super::*;
    use crate::types::{Role, States, WidgetLabel};
    use proptest::prelude::*;

    /// Strategy to generate a random Role.
    fn arb_role() -> impl Strategy<Value = Role> {
        prop_oneof![
            Just(Role::Button),
            Just(Role::CheckBox),
            (proptest::bool::ANY, proptest::bool::ANY)
                .prop_map(|(ro, ml)| Role::EditBox { read_only: ro, multiline: ml }),
            Just(Role::ListBox),
            Just(Role::Group),
            Just(Role::Label),
            Just(Role::TabControl),
        ]
    }

    /// Strategy to generate a random States bitflags (subset of the valid bits).
    fn arb_states() -> impl Strategy<Value = States> {
        (0u32..64).prop_map(States::from_bits_truncate)
    }

    fn make_label(name: &str, role: Role, states: States) -> WidgetLabel {
        let mut label = WidgetLabel::new(name, role);
        label.states = states;
        label
    }

    /// Build a random tree with `n` root nodes, each potentially having children.
    /// Returns the tree and the list of all inserted NodeIds.
    fn build_random_tree(
        roles: &[(Role, States)],
        children_per_node: &[usize],
    ) -> (Tree, Vec<NodeId>) {
        let mut tree = Tree::new();
        let mut all_ids = Vec::new();
        let mut child_idx = 0;

        // Insert root nodes
        for (i, (role, states)) in roles.iter().enumerate() {
            let label = make_label(&format!("node_{}", i), *role, *states);
            let id = tree.insert(None, i, label);
            all_ids.push(id);

            // Add children based on children_per_node
            if child_idx < children_per_node.len() {
                let num_children = children_per_node[child_idx] % 4; // 0-3 children
                child_idx += 1;
                for c in 0..num_children {
                    // Children are always focusable buttons to ensure interesting tests
                    let child_role = if c % 2 == 0 {
                        Role::Button
                    } else {
                        Role::CheckBox
                    };
                    let child_label = make_label(
                        &format!("child_{}_{}", i, c),
                        child_role,
                        States::empty(),
                    );
                    let child_id = tree.insert(Some(id), c, child_label);
                    all_ids.push(child_id);
                }
            }
        }

        (tree, all_ids)
    }

    proptest! {
        /// For any sequence of Tab presses, focus always lands on a
        /// valid, existing node (when the tree has focusable nodes).
        #[test]
        fn tab_next_always_lands_on_valid_node(
            roles in proptest::collection::vec(
                (arb_role(), arb_states()),
                1..10
            ),
            children_counts in proptest::collection::vec(0usize..8, 1..10),
            tab_count in 1usize..30,
        ) {
            let (tree, _all_ids) = build_random_tree(&roles, &children_counts);

            // Use the same DFS traversal the real code uses (respects HIDDEN subtrees)
            let focusable = collect_focusable_dfs(&tree);

            let mut current = None;
            for _ in 0..tab_count {
                let next = tab_next(&tree, current);
                if focusable.is_empty() {
                    // No focusable nodes — tab_next should return None
                    prop_assert_eq!(next, None);
                } else {
                    // Must return Some and the node must exist in the tree
                    prop_assert!(next.is_some(), "tab_next returned None but tree has focusable nodes");
                    let id = next.unwrap();
                    prop_assert!(tree.contains(id), "tab_next returned a node that doesn't exist in the tree");
                    // The node must be focusable
                    let node = tree.get(id).unwrap();
                    prop_assert!(
                        crate::types::is_focusable(node.label.role, node.label.states),
                        "tab_next returned a non-focusable node: {:?} {:?}",
                        node.label.role,
                        node.label.states
                    );
                }
                current = next;
            }
        }

        /// For any tree structure, tab_next cycles through all
        /// focusable nodes exactly once before repeating.
        #[test]
        fn tab_next_cycles_all_focusable_exactly_once(
            roles in proptest::collection::vec(
                (arb_role(), arb_states()),
                1..10
            ),
            children_counts in proptest::collection::vec(0usize..8, 1..10),
        ) {
            let (tree, _all_ids) = build_random_tree(&roles, &children_counts);

            // Use the same DFS traversal the real code uses (respects HIDDEN subtrees)
            let focusable = collect_focusable_dfs(&tree);

            if focusable.is_empty() {
                // Nothing to cycle through
                prop_assert_eq!(tab_next(&tree, None), None);
                return Ok(());
            }

            let n = focusable.len();
            let mut visited = Vec::with_capacity(n);
            let mut current = None;

            // Tab through N times — should visit each focusable node exactly once
            for _ in 0..n {
                current = tab_next(&tree, current);
                let id = current.unwrap();
                prop_assert!(
                    !visited.contains(&id),
                    "Node visited twice before completing the cycle"
                );
                visited.push(id);
            }

            // After N tabs, every focusable node should have been visited
            for &fid in &focusable {
                prop_assert!(
                    visited.contains(&fid),
                    "Focusable node was not visited during the full cycle"
                );
            }

            // The (N+1)th tab should wrap back to the first visited node
            let wrap = tab_next(&tree, current);
            prop_assert_eq!(
                wrap,
                Some(visited[0]),
                "After a full cycle, tab_next should wrap to the first focusable node"
            );
        }
    }
}
