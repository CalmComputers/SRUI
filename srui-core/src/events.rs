//! Output events — the core's entire output surface.
//!
//! The core never speaks. It emits structured events; readers (speech,
//! braille, UIA, test harnesses) consume them and render as they see fit.
//! The `speech` module provides the reference rendering.

use crate::tree::NodeId;
use crate::types::WidgetLabel;

/// A single item in the output stream.
#[derive(Debug, Clone, PartialEq)]
pub enum OutputEvent {
    /// What the user should perceive. Consumed by readers.
    Accessibility(AccessibilityEvent),
    /// What the program should react to. Consumed by application code.
    Widget(WidgetEvent),
    /// A ticker's interval elapsed (see `Ui::add_ticker`). Fires at most
    /// once per `set_now` call per ticker, so its resolution is the
    /// host's clock cadence.
    Tick { ticker: u64 },
}

/// Semantic events for the program: user intent, not perception.
#[derive(Debug, Clone, PartialEq)]
pub enum WidgetEvent {
    /// The node was activated (Enter on a button, or the layer's
    /// primary/cancel widget was triggered).
    Activated { node: NodeId },
    /// Secondary activation (Shift+Enter).
    SecondaryActivated { node: NodeId },
    /// A checkbox was toggled. `checked` is the new value.
    Toggled { node: NodeId, checked: bool },
    /// The node's widget state changed (text edited, selection moved,
    /// slider adjusted).
    Changed { node: NodeId },
}

/// Structured payload describing what the user should perceive. Each
/// variant carries the data its non-speech consumers (braille, UIA,
/// sonification) will need; the label snapshot is taken at emission time
/// so readers never race the tree.
#[derive(Debug, Clone, PartialEq)]
pub enum AccessibilityEvent {
    /// Focus moved to a different widget.
    Focused {
        node: NodeId,
        label: WidgetLabel,
        /// Names of Label-role siblings preceding the focused widget in
        /// child order. Empty on ordinary focus moves; populated when the
        /// host asks for a context re-announcement after a view
        /// transition.
        context_labels: Vec<String>,
    },

    /// Text was inserted or deleted in an editor. Deliberately carries
    /// only the perceptual content (what the user hears), not document
    /// text — readers that need the line or surrounding text query the
    /// editor through the node.
    Typing {
        node: NodeId,
        /// Single grapheme for `Insert`/`Delete`; empty for `DeleteWord`.
        grapheme: String,
        /// `Some(word)` when an insert just crossed a word boundary or
        /// when `kind` is `DeleteWord`.
        last_word: Option<String>,
        kind: TypingKind,
    },

    /// Cursor moved within text without an edit. Carries perceptual
    /// content only; query the editor for document text.
    TextNav {
        node: NodeId,
        /// Grapheme at the post-move cursor position.
        grapheme_at_cursor: String,
        /// Spoken context for the landing position at this granularity —
        /// the expanded character, the word, or the line. Composed by the
        /// widget because it depends on cursor-adjacent state.
        context: String,
        granularity: NavGranularity,
        /// `Some` when nav was attempted past an edge and the cursor
        /// did not move.
        boundary: Option<Boundary>,
    },

    /// Selection extended, contracted, cleared, or set-all.
    Selection {
        node: NodeId,
        /// The text added to or removed from the selection (or
        /// "{n} characters" for large selections).
        delta: String,
        kind: SelectionKind,
    },

    /// Discrete-value list/grid widget changed selection.
    ItemNav {
        node: NodeId,
        item: String,
        /// `(zero_based_index, total)`. `None` when the widget has no
        /// indexable concept.
        position: Option<(usize, usize)>,
        boundary: Option<Boundary>,
    },

    /// Tab control moved to a different tab. No `boundary` because tabs
    /// are circular.
    TabChange {
        node: NodeId,
        tab_name: String,
        position: (usize, usize),
    },

    /// Slider value changed.
    SliderChange {
        node: NodeId,
        value: i32,
        unit: String,
    },

    /// Filter / typeahead query changed; results recomputed.
    Filter {
        node: NodeId,
        query: String,
        first_result: Option<String>,
        result_count: usize,
    },

    /// Clipboard operation completed.
    Clipboard { node: NodeId, op: ClipboardOp },

    /// Free-form announcement — escape hatch for anything without a
    /// structured variant ("Nothing to delete", "no results", "blank").
    Announce { text: String },
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TypingKind {
    Insert,
    Delete,
    DeleteWord,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SelectionKind {
    Selected,
    Unselected,
    Cleared,
    All,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Boundary {
    Top,
    Bottom,
    Left,
    Right,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ClipboardOp {
    Copy,
    Cut,
    Paste,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum NavGranularity {
    Char,
    Word,
    LineEdge,
    TextEdge,
    LineUp,
    LineDown,
}

/// Apply coalescing rules to a drained batch.
///
/// State-describing accessibility events (`Focused`, `Selection`,
/// `ItemNav`, `TabChange`, `SliderChange`, `Filter`) keep only the last
/// occurrence of their kind — an intermediate focus move is discarded in
/// favor of the settled one. Action events (`Typing`, `TextNav`,
/// `Clipboard`, `Announce`) and all widget events keep emission order.
pub fn coalesce(events: Vec<OutputEvent>) -> Vec<OutputEvent> {
    use std::mem::discriminant;

    fn state_event(ev: &OutputEvent) -> Option<std::mem::Discriminant<AccessibilityEvent>> {
        match ev {
            OutputEvent::Accessibility(a) => match a {
                AccessibilityEvent::Focused { .. }
                | AccessibilityEvent::Selection { .. }
                | AccessibilityEvent::ItemNav { .. }
                | AccessibilityEvent::TabChange { .. }
                | AccessibilityEvent::SliderChange { .. }
                | AccessibilityEvent::Filter { .. } => Some(discriminant(a)),
                _ => None,
            },
            OutputEvent::Widget(_) | OutputEvent::Tick { .. } => None,
        }
    }

    // Pass 1: last occurrence index per state-event kind.
    let mut last_idx: Vec<(std::mem::Discriminant<AccessibilityEvent>, usize)> = Vec::new();
    for (i, ev) in events.iter().enumerate() {
        if let Some(d) = state_event(ev) {
            if let Some(slot) = last_idx.iter_mut().find(|(dd, _)| *dd == d) {
                slot.1 = i;
            } else {
                last_idx.push((d, i));
            }
        }
    }

    // Pass 2: drop state events that aren't the last of their kind.
    events
        .into_iter()
        .enumerate()
        .filter(|(i, ev)| match state_event(ev) {
            None => true,
            Some(d) => last_idx
                .iter()
                .find(|(dd, _)| *dd == d)
                .map(|(_, last)| *last == *i)
                .unwrap_or(true),
        })
        .map(|(_, ev)| ev)
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::tree::Tree;
    use crate::types::{Role, WidgetLabel};

    fn focused_event(tree: &mut Tree, name: &str) -> OutputEvent {
        let id = tree.insert(None, usize::MAX, WidgetLabel::new(name, Role::Button));
        OutputEvent::Accessibility(AccessibilityEvent::Focused {
            node: id,
            label: WidgetLabel::new(name, Role::Button),
            context_labels: Vec::new(),
        })
    }

    #[test]
    fn coalesce_keeps_last_focused() {
        let mut tree = Tree::new();
        let a = focused_event(&mut tree, "A");
        let b = focused_event(&mut tree, "B");
        let out = coalesce(vec![a, b.clone()]);
        assert_eq!(out, vec![b]);
    }

    #[test]
    fn coalesce_keeps_all_announces() {
        let one = OutputEvent::Accessibility(AccessibilityEvent::Announce { text: "one".into() });
        let two = OutputEvent::Accessibility(AccessibilityEvent::Announce { text: "two".into() });
        let out = coalesce(vec![one.clone(), two.clone()]);
        assert_eq!(out, vec![one, two]);
    }

    #[test]
    fn coalesce_preserves_widget_events() {
        let mut tree = Tree::new();
        let a = focused_event(&mut tree, "A");
        let b = focused_event(&mut tree, "B");
        let id = tree.insert(None, usize::MAX, WidgetLabel::new("X", Role::Button));
        let w = OutputEvent::Widget(WidgetEvent::Activated { node: id });
        let out = coalesce(vec![a, w.clone(), b.clone()]);
        assert_eq!(out, vec![w, b]);
    }

    #[test]
    fn coalesce_is_per_kind_not_global() {
        let mut tree = Tree::new();
        let f = focused_event(&mut tree, "A");
        let id = tree.insert(None, usize::MAX, WidgetLabel::new("L", Role::ListBox));
        let item = OutputEvent::Accessibility(AccessibilityEvent::ItemNav {
            node: id,
            item: "first".into(),
            position: Some((0, 3)),
            boundary: None,
        });
        // Focused and ItemNav are different kinds — both survive.
        let out = coalesce(vec![f.clone(), item.clone()]);
        assert_eq!(out, vec![f, item]);
    }
}
