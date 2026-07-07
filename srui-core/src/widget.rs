//! Widget behavior — trait-dispatched input handling per node.
//!
//! A `Widget` owns a node's interaction state and handles logical input
//! directed at the node while it is focused. Built-in behaviors live
//! here; custom widgets are Rust-only (bindings compose primitives and
//! subclass their wrapper classes instead — see docs/architecture.md,
//! section 5).

use std::any::Any;

use crate::events::{AccessibilityEvent, Boundary, OutputEvent, WidgetEvent};
use crate::input::LogicalInput;
use crate::tree::NodeId;
use crate::types::WidgetLabel;

/// Timeout for resetting the typeahead buffer (milliseconds of host time).
const TYPE_AHEAD_TIMEOUT_MS: u64 = 400;

/// Everything a widget may touch while handling input: its own node id,
/// its label (kept in sync with widget state), the output queue, and the
/// host-supplied monotonic clock (see `Ui::set_now`).
pub struct WidgetCtx<'a> {
    pub node: NodeId,
    pub label: &'a mut WidgetLabel,
    pub events: &'a mut Vec<OutputEvent>,
    pub now_ms: u64,
}

impl WidgetCtx<'_> {
    pub fn emit_widget(&mut self, event: WidgetEvent) {
        self.events.push(OutputEvent::Widget(event));
    }

    pub fn emit_accessibility(&mut self, event: AccessibilityEvent) {
        self.events.push(OutputEvent::Accessibility(event));
    }

    pub fn announce(&mut self, text: impl Into<String>) {
        self.emit_accessibility(AccessibilityEvent::Announce { text: text.into() });
    }
}

/// Trait-dispatched behavior attached to a node.
pub trait Widget: Any {
    /// Handle a logical input directed at this node while focused.
    /// Return true if the input was consumed; unconsumed input falls
    /// through to bindings and framework navigation.
    fn handle_input(&mut self, input: &LogicalInput, ctx: &mut WidgetCtx) -> bool;

    fn as_any(&self) -> &dyn Any;
    fn as_any_mut(&mut self) -> &mut dyn Any;
}

/// Button — Enter or Space activates.
#[derive(Debug, Default)]
pub struct Button;

impl Widget for Button {
    fn handle_input(&mut self, input: &LogicalInput, ctx: &mut WidgetCtx) -> bool {
        match input {
            LogicalInput::Activate | LogicalInput::TypeChar(' ') => {
                ctx.emit_widget(WidgetEvent::Activated { node: ctx.node });
                true
            }
            LogicalInput::SecondaryActivate => {
                ctx.emit_widget(WidgetEvent::SecondaryActivated { node: ctx.node });
                true
            }
            _ => false,
        }
    }

    fn as_any(&self) -> &dyn Any {
        self
    }
    fn as_any_mut(&mut self) -> &mut dyn Any {
        self
    }
}

/// CheckBox — Space toggles. Enter deliberately falls through so it can
/// reach the layer's primary widget (Windows dialog convention).
#[derive(Debug, Default)]
pub struct CheckBox {
    pub checked: bool,
}

impl CheckBox {
    pub fn new(checked: bool) -> Self {
        Self { checked }
    }

    /// The label value for a given checked state.
    pub fn value_text(checked: bool) -> &'static str {
        if checked {
            "checked"
        } else {
            "not checked"
        }
    }
}

impl Widget for CheckBox {
    fn handle_input(&mut self, input: &LogicalInput, ctx: &mut WidgetCtx) -> bool {
        match input {
            LogicalInput::TypeChar(' ') => {
                self.checked = !self.checked;
                ctx.label.value = Self::value_text(self.checked).to_string();
                ctx.emit_widget(WidgetEvent::Toggled {
                    node: ctx.node,
                    checked: self.checked,
                });
                ctx.announce(Self::value_text(self.checked));
                true
            }
            _ => false,
        }
    }

    fn as_any(&self) -> &dyn Any {
        self
    }
    fn as_any_mut(&mut self) -> &mut dyn Any {
        self
    }
}

/// Typeahead buffer for list widgets, driven by the host clock.
#[derive(Debug, Default)]
struct TypeAhead {
    buffer: String,
    last_keystroke_ms: Option<u64>,
}

/// ListBox — a single-selection list. Arrows move the selection with
/// boundary announcements, Home/End jump, printable characters do
/// first-letter cycling and multi-letter prefix search. Enter is
/// deliberately not claimed: it falls through to the layer's primary
/// widget (Windows dialog convention); programs that want direct
/// item-activation semantics bind their own shortcut.
#[derive(Debug, Default)]
pub struct ListBox {
    items: Vec<String>,
    selected: usize,
    /// When true, announcements and focus state_text carry "N of M".
    numbered: bool,
    type_ahead: TypeAhead,
}

impl ListBox {
    pub fn new(items: Vec<String>, numbered: bool) -> Self {
        Self {
            items,
            selected: 0,
            numbered,
            type_ahead: TypeAhead::default(),
        }
    }

    pub fn items(&self) -> &[String] {
        &self.items
    }

    pub fn selected(&self) -> usize {
        self.selected
    }

    pub fn selected_item(&self) -> Option<&str> {
        self.items.get(self.selected).map(|s| s.as_str())
    }

    /// Replace the item list, clamping the selection.
    pub(crate) fn set_items(&mut self, items: Vec<String>, label: &mut WidgetLabel) {
        self.items = items;
        if !self.items.is_empty() && self.selected >= self.items.len() {
            self.selected = self.items.len() - 1;
        }
        self.sync_label(label);
    }

    /// Move the selection programmatically (clamped).
    pub(crate) fn set_selected(&mut self, index: usize, label: &mut WidgetLabel) {
        if self.items.is_empty() {
            return;
        }
        self.selected = index.min(self.items.len() - 1);
        self.sync_label(label);
    }

    /// Keep the golden six in step with the widget state: value is the
    /// selected item (or "empty"), state_text is "N of M" when numbered.
    pub(crate) fn sync_label(&self, label: &mut WidgetLabel) {
        match self.items.get(self.selected) {
            Some(item) => {
                label.value = item.clone();
                if self.numbered {
                    label.state_text = format!("{} of {}", self.selected + 1, self.items.len());
                }
            }
            None => {
                label.value = "empty".to_string();
                if self.numbered {
                    label.state_text.clear();
                }
            }
        }
    }

    /// Emit an ItemNav for the current selection.
    fn emit_item(&self, ctx: &mut WidgetCtx, boundary: Option<Boundary>) {
        let item = self.items[self.selected].clone();
        let position = if self.numbered {
            Some((self.selected, self.items.len()))
        } else {
            None
        };
        ctx.emit_accessibility(AccessibilityEvent::ItemNav {
            node: ctx.node,
            item,
            position,
            boundary,
        });
    }

    /// Selection moved by input: sync label, announce, notify the program.
    fn select_and_announce(&mut self, index: usize, ctx: &mut WidgetCtx) {
        self.selected = index;
        self.sync_label(ctx.label);
        self.emit_item(ctx, None);
        ctx.emit_widget(WidgetEvent::Changed { node: ctx.node });
    }

    fn handle_type_ahead(&mut self, ch: char, ctx: &mut WidgetCtx) {
        let ch_lower = ch.to_ascii_lowercase();
        let ta = &mut self.type_ahead;

        let should_reset = match ta.last_keystroke_ms {
            Some(last) => ctx.now_ms.saturating_sub(last) > TYPE_AHEAD_TIMEOUT_MS,
            None => true,
        };
        // Cycling: the same letter typed repeatedly.
        let cycling = !ta.buffer.is_empty() && ta.buffer.chars().all(|c| c == ch_lower);

        if should_reset || cycling {
            ta.buffer.clear();
        }
        ta.buffer.push(ch_lower);
        ta.last_keystroke_ms = Some(ctx.now_ms);

        if cycling || self.type_ahead.buffer.len() == 1 {
            // Single char: cycle from current position forward.
            let count = self.items.len();
            for offset in 1..=count {
                let idx = (self.selected + offset) % count;
                if self.items[idx]
                    .chars()
                    .next()
                    .map(|c| c.to_ascii_lowercase() == ch_lower)
                    .unwrap_or(false)
                {
                    self.select_and_announce(idx, ctx);
                    break;
                }
            }
        } else {
            // Multi-letter prefix search with wraparound, current item included.
            let needle = self.type_ahead.buffer.clone();
            let count = self.items.len();
            for offset in 0..count {
                let idx = (self.selected + offset) % count;
                if self.items[idx].to_ascii_lowercase().starts_with(&needle) {
                    if idx != self.selected {
                        self.select_and_announce(idx, ctx);
                    } else {
                        self.emit_item(ctx, None);
                    }
                    break;
                }
            }
        }
    }
}

impl Widget for ListBox {
    fn handle_input(&mut self, input: &LogicalInput, ctx: &mut WidgetCtx) -> bool {
        if self.items.is_empty() {
            return false;
        }
        match input {
            LogicalInput::MoveDown => {
                if self.selected + 1 < self.items.len() {
                    self.select_and_announce(self.selected + 1, ctx);
                } else {
                    self.emit_item(ctx, Some(Boundary::Bottom));
                }
                true
            }
            LogicalInput::MoveUp => {
                if self.selected > 0 {
                    self.select_and_announce(self.selected - 1, ctx);
                } else {
                    self.emit_item(ctx, Some(Boundary::Top));
                }
                true
            }
            LogicalInput::MoveToDocStart | LogicalInput::MoveToLineStart => {
                if self.selected != 0 {
                    self.select_and_announce(0, ctx);
                }
                true
            }
            LogicalInput::MoveToDocEnd | LogicalInput::MoveToLineEnd => {
                let last = self.items.len() - 1;
                if self.selected != last {
                    self.select_and_announce(last, ctx);
                }
                true
            }
            LogicalInput::TypeChar(ch) => {
                self.handle_type_ahead(*ch, ctx);
                true
            }
            _ => false,
        }
    }

    fn as_any(&self) -> &dyn Any {
        self
    }
    fn as_any_mut(&mut self) -> &mut dyn Any {
        self
    }
}
