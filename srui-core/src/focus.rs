//! Focus memory — remembers which child was last focused in each container.

use std::collections::HashMap;

use crate::tree::{NodeId, Tree};

/// Remembers the last-focused child per container so re-entering a
/// container restores position. Keyed by NodeId: entries die with their
/// nodes and are swept by `gc`.
#[derive(Debug, Default)]
pub struct FocusMemory {
    /// container node -> last focused child node
    memory: HashMap<NodeId, NodeId>,
}

impl FocusMemory {
    pub fn new() -> Self {
        Self::default()
    }

    /// Record that `child` was the last focused widget inside `container`.
    pub fn remember(&mut self, container: NodeId, child: NodeId) {
        self.memory.insert(container, child);
    }

    /// Recall the last focused child of `container`.
    pub fn recall(&self, container: NodeId) -> Option<NodeId> {
        self.memory.get(&container).copied()
    }

    /// Remove entries whose container or child no longer exists in the tree.
    pub fn gc(&mut self, tree: &Tree) {
        self.memory
            .retain(|container, child| tree.contains(*container) && tree.contains(*child));
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::types::{Role, WidgetLabel};

    #[test]
    fn remember_and_recall() {
        let mut tree = Tree::new();
        let group = tree.insert(None, 0, WidgetLabel::new("G", Role::Group));
        let a = tree.insert(Some(group), 0, WidgetLabel::new("A", Role::Button));
        let b = tree.insert(Some(group), 1, WidgetLabel::new("B", Role::Button));

        let mut memory = FocusMemory::new();
        assert_eq!(memory.recall(group), None);

        memory.remember(group, a);
        assert_eq!(memory.recall(group), Some(a));

        memory.remember(group, b);
        assert_eq!(memory.recall(group), Some(b));
    }

    #[test]
    fn gc_drops_dead_entries() {
        let mut tree = Tree::new();
        let group = tree.insert(None, 0, WidgetLabel::new("G", Role::Group));
        let a = tree.insert(Some(group), 0, WidgetLabel::new("A", Role::Button));

        let mut memory = FocusMemory::new();
        memory.remember(group, a);

        tree.remove(a);
        memory.gc(&tree);
        assert_eq!(memory.recall(group), None);
    }

    #[test]
    fn gc_drops_entry_when_container_removed() {
        let mut tree = Tree::new();
        let group = tree.insert(None, 0, WidgetLabel::new("G", Role::Group));
        let a = tree.insert(Some(group), 0, WidgetLabel::new("A", Role::Button));

        let mut memory = FocusMemory::new();
        memory.remember(group, a);

        // Removing the container removes the child with it
        tree.remove(group);
        memory.gc(&tree);
        assert_eq!(memory.recall(group), None);
    }
}
