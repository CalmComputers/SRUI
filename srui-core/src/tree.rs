//! The semantic tree — retained, handle-addressed, layered for modals.

use slotmap::{new_key_type, Key as SlotKey, KeyData, SlotMap};
use smallvec::SmallVec;

use crate::types::WidgetLabel;

new_key_type! {
    /// Unique identifier for a node in the tree. Generational: a NodeId
    /// held after its node is removed resolves to nothing, never to a
    /// reused slot.
    pub struct NodeId;
}

impl NodeId {
    /// Lossless u64 form for the FFI boundary.
    pub fn to_ffi(self) -> u64 {
        self.data().as_ffi()
    }

    /// Reconstruct from the u64 form. A value that never came from
    /// `to_ffi` yields an id that resolves to nothing.
    pub fn from_ffi(raw: u64) -> Self {
        KeyData::from_ffi(raw).into()
    }
}

/// A single node in the semantic tree.
#[derive(Debug)]
pub struct Node {
    pub id: NodeId,
    pub parent: Option<NodeId>,
    pub children: SmallVec<[NodeId; 8]>,
    pub label: WidgetLabel,
}

/// A layer in the tree's modal stack. Each layer has its own roots and focus.
struct Layer {
    roots: SmallVec<[NodeId; 4]>,
    focus: Option<NodeId>,
    primary: Option<NodeId>,
    cancel: Option<NodeId>,
}

impl Layer {
    fn new() -> Self {
        Self {
            roots: SmallVec::new(),
            focus: None,
            primary: None,
            cancel: None,
        }
    }
}

/// The semantic tree. Owns all nodes via a SlotMap for O(1) access.
/// Layers stack: dialogs/palettes push a layer; the active layer is always last.
pub struct Tree {
    nodes: SlotMap<NodeId, Node>,
    layers: Vec<Layer>,
}

impl Tree {
    pub fn new() -> Self {
        Self {
            nodes: SlotMap::with_key(),
            layers: vec![Layer::new()],
        }
    }

    /// Insert a new node as a child of `parent` at position `index`.
    /// If `parent` is None, inserts as a root of the active layer.
    pub fn insert(
        &mut self,
        parent: Option<NodeId>,
        index: usize,
        label: WidgetLabel,
    ) -> NodeId {
        let id = self.nodes.insert_with_key(|id| Node {
            id,
            parent,
            children: SmallVec::new(),
            label,
        });

        match parent {
            Some(parent_id) => {
                if let Some(parent_node) = self.nodes.get_mut(parent_id) {
                    let idx = index.min(parent_node.children.len());
                    parent_node.children.insert(idx, id);
                }
            }
            None => {
                let layer = self.active_layer_mut();
                let idx = index.min(layer.roots.len());
                layer.roots.insert(idx, id);
            }
        }

        id
    }

    /// Remove a node and all its descendants from the tree.
    pub fn remove(&mut self, id: NodeId) {
        // Collect all descendants first
        let mut to_remove = Vec::new();
        self.collect_subtree(id, &mut to_remove);

        // Remove from parent's children list or active layer's roots
        if let Some(node) = self.nodes.get(id) {
            if let Some(parent_id) = node.parent {
                if let Some(parent) = self.nodes.get_mut(parent_id) {
                    parent.children.retain(|child| *child != id);
                }
            } else {
                self.active_layer_mut().roots.retain(|root| *root != id);
            }
        }

        // Remove all nodes in subtree; clear focus if any removed node was focused
        {
            let focus = &mut self.active_layer_mut().focus;
            if let Some(fid) = *focus {
                if to_remove.contains(&fid) {
                    *focus = None;
                }
            }
        }
        for nid in to_remove {
            self.nodes.remove(nid);
        }
    }

    fn collect_subtree(&self, id: NodeId, out: &mut Vec<NodeId>) {
        out.push(id);
        if let Some(node) = self.nodes.get(id) {
            for &child in &node.children {
                self.collect_subtree(child, out);
            }
        }
    }

    /// Remove nodes from the SlotMap without touching any layer's roots list.
    fn remove_subtree_nodes(&mut self, id: NodeId) {
        let mut to_remove = Vec::new();
        self.collect_subtree(id, &mut to_remove);
        for nid in to_remove {
            self.nodes.remove(nid);
        }
    }

    pub fn get(&self, id: NodeId) -> Option<&Node> {
        self.nodes.get(id)
    }

    pub fn get_mut(&mut self, id: NodeId) -> Option<&mut Node> {
        self.nodes.get_mut(id)
    }

    pub fn children(&self, id: NodeId) -> &[NodeId] {
        self.nodes
            .get(id)
            .map(|n| n.children.as_slice())
            .unwrap_or(&[])
    }

    pub fn parent(&self, id: NodeId) -> Option<NodeId> {
        self.nodes.get(id).and_then(|n| n.parent)
    }

    pub fn focus(&self) -> Option<NodeId> {
        self.active_layer().focus
    }

    pub fn set_focus(&mut self, id: NodeId) {
        self.active_layer_mut().focus = Some(id);
    }

    pub fn clear_focus(&mut self) {
        self.active_layer_mut().focus = None;
    }

    pub fn roots(&self) -> &[NodeId] {
        &self.active_layer().roots
    }

    pub fn is_empty(&self) -> bool {
        self.nodes.is_empty()
    }

    pub fn len(&self) -> usize {
        self.nodes.len()
    }

    /// Check if a node exists in the tree.
    pub fn contains(&self, id: NodeId) -> bool {
        self.nodes.contains_key(id)
    }

    // ── Layer stack ──

    /// Push an empty layer onto the stack. New root nodes go into this layer.
    pub fn push_layer(&mut self) {
        self.layers.push(Layer::new());
    }

    /// Pop the top layer, removing all its nodes from the SlotMap.
    /// Returns the restored (now-active) layer's focus.
    /// Panics if only the base layer remains.
    pub fn pop_layer(&mut self) -> Option<NodeId> {
        assert!(self.layers.len() > 1, "Cannot pop the base layer");
        let layer = self.layers.pop().unwrap();
        // Remove all nodes that belonged to the popped layer
        for root_id in layer.roots {
            self.remove_subtree_nodes(root_id);
        }
        // Return the now-active layer's focus
        self.active_layer().focus
    }

    /// How many layers are on the stack.
    pub fn layer_depth(&self) -> usize {
        self.layers.len()
    }

    // ── Primary / cancel ──

    /// Get the primary widget id for the active layer.
    pub fn primary(&self) -> Option<NodeId> {
        self.active_layer().primary
    }

    /// Set the primary widget for the active layer (Enter activates it).
    pub fn set_primary(&mut self, id: NodeId) {
        self.active_layer_mut().primary = Some(id);
    }

    /// Get the cancel widget id for the active layer.
    pub fn cancel(&self) -> Option<NodeId> {
        self.active_layer().cancel
    }

    /// Set the cancel widget for the active layer (Escape activates it).
    pub fn set_cancel(&mut self, id: NodeId) {
        self.active_layer_mut().cancel = Some(id);
    }

    fn active_layer(&self) -> &Layer {
        self.layers.last().expect("layer stack is never empty")
    }

    fn active_layer_mut(&mut self) -> &mut Layer {
        self.layers.last_mut().expect("layer stack is never empty")
    }
}

impl Default for Tree {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::types::Role;

    fn make_label(name: &str, role: Role) -> WidgetLabel {
        WidgetLabel::new(name, role)
    }

    #[test]
    fn insert_root_nodes() {
        let mut tree = Tree::new();
        let a = tree.insert(None, 0, make_label("A", Role::Button));
        let b = tree.insert(None, 1, make_label("B", Role::Button));

        assert_eq!(tree.roots(), &[a, b]);
        assert_eq!(tree.len(), 2);
        assert!(tree.get(a).is_some());
        assert!(tree.get(b).is_some());
    }

    #[test]
    fn insert_children() {
        let mut tree = Tree::new();
        let parent = tree.insert(None, 0, make_label("Group", Role::Group));
        let child1 = tree.insert(Some(parent), 0, make_label("A", Role::Button));
        let child2 = tree.insert(Some(parent), 1, make_label("B", Role::Button));

        assert_eq!(tree.children(parent), &[child1, child2]);
        assert_eq!(tree.parent(child1), Some(parent));
        assert_eq!(tree.parent(child2), Some(parent));
    }

    #[test]
    fn remove_subtree() {
        let mut tree = Tree::new();
        let parent = tree.insert(None, 0, make_label("Group", Role::Group));
        let child = tree.insert(Some(parent), 0, make_label("A", Role::Button));
        let grandchild = tree.insert(Some(child), 0, make_label("B", Role::Button));

        tree.remove(child);

        assert_eq!(tree.len(), 1); // only parent remains
        assert!(tree.get(child).is_none());
        assert!(tree.get(grandchild).is_none());
        assert!(tree.children(parent).is_empty());
    }

    #[test]
    fn remove_clears_focus() {
        let mut tree = Tree::new();
        let a = tree.insert(None, 0, make_label("A", Role::Button));
        tree.set_focus(a);
        assert_eq!(tree.focus(), Some(a));

        tree.remove(a);
        assert_eq!(tree.focus(), None);
    }

    #[test]
    fn focus_lifecycle() {
        let mut tree = Tree::new();
        assert_eq!(tree.focus(), None);

        let a = tree.insert(None, 0, make_label("A", Role::Button));
        tree.set_focus(a);
        assert_eq!(tree.focus(), Some(a));

        tree.clear_focus();
        assert_eq!(tree.focus(), None);
    }

    #[test]
    fn stale_id_resolves_to_nothing() {
        let mut tree = Tree::new();
        let a = tree.insert(None, 0, make_label("A", Role::Button));
        tree.remove(a);
        // Slot may be reused by a new node, but the old id must not resolve
        let b = tree.insert(None, 0, make_label("B", Role::Button));
        assert!(tree.get(a).is_none());
        assert!(tree.get(b).is_some());
    }

    #[test]
    fn node_id_ffi_roundtrip() {
        let mut tree = Tree::new();
        let a = tree.insert(None, 0, make_label("A", Role::Button));
        let raw = a.to_ffi();
        assert_eq!(NodeId::from_ffi(raw), a);
        assert!(tree.get(NodeId::from_ffi(raw)).is_some());
    }

    // ── Layer stack tests ──

    #[test]
    fn push_creates_isolated_roots() {
        let mut tree = Tree::new();
        let a = tree.insert(None, 0, make_label("A", Role::Button));
        tree.set_focus(a);

        tree.push_layer();
        // Active layer has no roots
        assert!(tree.roots().is_empty());
        assert_eq!(tree.focus(), None);

        let b = tree.insert(None, 0, make_label("B", Role::Button));
        assert_eq!(tree.roots(), &[b]);

        // But both nodes exist in the shared SlotMap
        assert!(tree.get(a).is_some());
        assert!(tree.get(b).is_some());
    }

    #[test]
    fn pop_removes_nodes_from_slotmap() {
        let mut tree = Tree::new();
        let a = tree.insert(None, 0, make_label("A", Role::Button));
        tree.set_focus(a);

        tree.push_layer();
        let b = tree.insert(None, 0, make_label("B", Role::Button));
        let c = tree.insert(Some(b), 0, make_label("C", Role::Button));

        assert_eq!(tree.len(), 3);

        tree.pop_layer();

        // b and c removed from SlotMap
        assert!(tree.get(b).is_none());
        assert!(tree.get(c).is_none());
        // a still exists
        assert!(tree.get(a).is_some());
        assert_eq!(tree.len(), 1);
    }

    #[test]
    fn pop_restores_focus() {
        let mut tree = Tree::new();
        let a = tree.insert(None, 0, make_label("A", Role::Button));
        tree.set_focus(a);

        tree.push_layer();
        let b = tree.insert(None, 0, make_label("B", Role::Button));
        tree.set_focus(b);

        let restored = tree.pop_layer();
        assert_eq!(restored, Some(a));
        assert_eq!(tree.focus(), Some(a));
    }

    #[test]
    fn cross_layer_get_works() {
        let mut tree = Tree::new();
        let a = tree.insert(None, 0, make_label("A", Role::Button));

        tree.push_layer();
        // Can still access node from base layer
        assert!(tree.get(a).is_some());
        assert_eq!(tree.get(a).unwrap().label.name.as_deref(), Some("A"));
    }

    #[test]
    #[should_panic(expected = "Cannot pop the base layer")]
    fn cannot_pop_base_layer() {
        let mut tree = Tree::new();
        tree.pop_layer();
    }

    #[test]
    fn layer_depth() {
        let mut tree = Tree::new();
        assert_eq!(tree.layer_depth(), 1);

        tree.push_layer();
        assert_eq!(tree.layer_depth(), 2);

        tree.push_layer();
        assert_eq!(tree.layer_depth(), 3);

        tree.pop_layer();
        assert_eq!(tree.layer_depth(), 2);
    }
}
