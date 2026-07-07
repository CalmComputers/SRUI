//! The Ui context — ties the tree, widget dispatch, focus, and the
//! output event queue together.
//!
//! The host drives it: push logical input in with `handle_input`, mutate
//! the tree through the insertion/removal methods, and drain coalesced
//! output events when convenient (typically after each input event).

use slotmap::SecondaryMap;

use crate::clipboard::{Clipboard, NoClipboard};
use crate::events::{self, AccessibilityEvent, OutputEvent, WidgetEvent};
use crate::focus::FocusMemory;
use crate::input::LogicalInput;
use crate::nav::{self, TreeDirection};
use crate::tree::{NodeId, Tree};
use crate::types::{is_focusable, Role, States, WidgetLabel};
use crate::widget::{
    Button, CheckBox, EditBox, FilterListBox, ListBox, ShortcutField, Slider, TabControl, Widget,
    WidgetCtx,
};

pub struct Ui {
    tree: Tree,
    widgets: SecondaryMap<NodeId, Box<dyn Widget>>,
    focus_memory: FocusMemory,
    events: Vec<OutputEvent>,
    /// Host-supplied monotonic clock in milliseconds. Drives typeahead
    /// timeouts and tickers; without `set_now` calls, neither fires.
    now_ms: u64,
    /// Injected platform clipboard; every editbox gets it for free.
    clipboard: Box<dyn Clipboard>,
    tickers: Vec<Ticker>,
    next_ticker_id: u64,
}

struct Ticker {
    id: u64,
    interval_ms: u64,
    next_fire_ms: u64,
}

impl Ui {
    pub fn new() -> Self {
        Self {
            tree: Tree::new(),
            widgets: SecondaryMap::new(),
            focus_memory: FocusMemory::new(),
            events: Vec::new(),
            now_ms: 0,
            clipboard: Box::new(NoClipboard),
            tickers: Vec::new(),
            next_ticker_id: 0,
        }
    }

    /// Install a platform clipboard (defaults to a no-op).
    pub fn set_clipboard(&mut self, clipboard: Box<dyn Clipboard>) {
        self.clipboard = clipboard;
    }

    /// Advance the host clock (monotonic milliseconds). Call every loop
    /// iteration (not just on input): typeahead timeouts and tickers are
    /// checked here, so ticker resolution is the call cadence.
    pub fn set_now(&mut self, now_ms: u64) {
        self.now_ms = now_ms;
        for ticker in &mut self.tickers {
            if now_ms >= ticker.next_fire_ms {
                self.events.push(OutputEvent::Tick { ticker: ticker.id });
                // Drift-tolerant: the next interval starts now, so a late
                // check fires once rather than bursting to catch up.
                ticker.next_fire_ms = now_ms + ticker.interval_ms;
            }
        }
    }

    /// Register a periodic ticker: a `Tick` event fires each time
    /// `interval_ms` elapses, observed at `set_now` resolution. Returns
    /// the id carried by the events.
    pub fn add_ticker(&mut self, interval_ms: u64) -> u64 {
        self.next_ticker_id += 1;
        let interval = interval_ms.max(1);
        self.tickers.push(Ticker {
            id: self.next_ticker_id,
            interval_ms: interval,
            next_fire_ms: self.now_ms + interval,
        });
        self.next_ticker_id
    }

    pub fn remove_ticker(&mut self, id: u64) {
        self.tickers.retain(|t| t.id != id);
    }

    // ── Tree construction ──

    /// Insert a behavior-less node (groups, labels) at the end of the
    /// parent's children (or the active layer's roots).
    pub fn insert(&mut self, parent: Option<NodeId>, label: WidgetLabel) -> NodeId {
        self.tree.insert(parent, usize::MAX, label)
    }

    /// Insert a behavior-less node at a specific child position.
    pub fn insert_at(&mut self, parent: Option<NodeId>, index: usize, label: WidgetLabel) -> NodeId {
        self.tree.insert(parent, index, label)
    }

    /// Insert a node with widget behavior at the end of the parent's children.
    pub fn insert_widget(
        &mut self,
        parent: Option<NodeId>,
        label: WidgetLabel,
        widget: Box<dyn Widget>,
    ) -> NodeId {
        let id = self.tree.insert(parent, usize::MAX, label);
        self.widgets.insert(id, widget);
        id
    }

    /// Insert a node with widget behavior at a specific child position.
    pub fn insert_widget_at(
        &mut self,
        parent: Option<NodeId>,
        index: usize,
        label: WidgetLabel,
        widget: Box<dyn Widget>,
    ) -> NodeId {
        let id = self.tree.insert(parent, index, label);
        self.widgets.insert(id, widget);
        id
    }

    /// Convenience: a button.
    pub fn button(&mut self, parent: Option<NodeId>, name: impl Into<String>) -> NodeId {
        self.insert_widget(parent, WidgetLabel::new(name, Role::Button), Box::new(Button))
    }

    /// Convenience: a checkbox with an initial value.
    pub fn checkbox(
        &mut self,
        parent: Option<NodeId>,
        name: impl Into<String>,
        checked: bool,
    ) -> NodeId {
        let mut label = WidgetLabel::new(name, Role::CheckBox);
        label.value = CheckBox::value_text(checked).to_string();
        self.insert_widget(parent, label, Box::new(CheckBox::new(checked)))
    }

    /// Convenience: a single-selection listbox. `numbered` adds "N of M"
    /// to announcements and the focus state text.
    pub fn listbox(
        &mut self,
        parent: Option<NodeId>,
        name: impl Into<String>,
        items: Vec<String>,
        numbered: bool,
    ) -> NodeId {
        let widget = ListBox::new(items, numbered);
        let mut label = WidgetLabel::new(name, Role::ListBox);
        widget.sync_label(&mut label);
        self.insert_widget(parent, label, Box::new(widget))
    }

    /// Replace a listbox's items (selection clamped). Re-announces if the
    /// listbox is focused and its label changed.
    pub fn set_list_items(&mut self, id: NodeId, items: Vec<String>) {
        self.with_widget::<ListBox>(id, |list, label| list.set_items(items, label));
    }

    /// Move a listbox's selection programmatically (clamped). Re-announces
    /// if the listbox is focused and its label changed.
    pub fn set_list_selected(&mut self, id: NodeId, index: usize) {
        self.with_widget::<ListBox>(id, |list, label| list.set_selected(index, label));
    }

    /// Mutate a node's widget state and label together; re-announces if
    /// the node is focused and the label changed. All the typed `set_*`
    /// mutators route through this.
    fn with_widget<T: Widget>(&mut self, id: NodeId, f: impl FnOnce(&mut T, &mut WidgetLabel)) {
        let Some(widget) = self.widgets.get_mut(id) else {
            return;
        };
        let Some(typed) = widget.as_any_mut().downcast_mut::<T>() else {
            return;
        };
        let Some(node) = self.tree.get_mut(id) else {
            return;
        };
        let before = node.label.clone();
        f(typed, &mut node.label);
        if self.tree.focus() == Some(id) && self.tree.get(id).unwrap().label != before {
            self.emit_focused(id);
        }
    }

    /// Convenience: a slider. Configure steps/unit by inserting a
    /// customized `Slider` via `insert_widget` instead.
    pub fn slider(
        &mut self,
        parent: Option<NodeId>,
        name: impl Into<String>,
        value: i32,
        min: i32,
        max: i32,
    ) -> NodeId {
        self.slider_widget(parent, name, Slider::new(value, min, max))
    }

    /// Insert a pre-configured slider (steps, unit).
    pub fn slider_widget(
        &mut self,
        parent: Option<NodeId>,
        name: impl Into<String>,
        slider: Slider,
    ) -> NodeId {
        let mut label = WidgetLabel::new(name, Role::Slider);
        slider.sync_label(&mut label);
        self.insert_widget(parent, label, Box::new(slider))
    }

    /// Move a slider programmatically (clamped). Re-announces if focused.
    pub fn set_slider_value(&mut self, id: NodeId, value: i32) {
        self.with_widget::<Slider>(id, |slider, label| slider.set_value(value, label));
    }

    /// Convenience: a tab control.
    pub fn tab_control(
        &mut self,
        parent: Option<NodeId>,
        name: impl Into<String>,
        tabs: Vec<String>,
        active: usize,
    ) -> NodeId {
        let widget = TabControl::new(tabs, active);
        let mut label = WidgetLabel::new(name, Role::TabControl);
        widget.sync_label(&mut label);
        self.insert_widget(parent, label, Box::new(widget))
    }

    /// Switch a tab control programmatically (clamped). Re-announces if
    /// focused.
    pub fn set_active_tab(&mut self, id: NodeId, index: usize) {
        self.with_widget::<TabControl>(id, |tc, label| tc.set_active(index, label));
    }

    /// Convenience: a shortcut-capture field.
    pub fn shortcut_field(&mut self, parent: Option<NodeId>, name: impl Into<String>) -> NodeId {
        let widget = ShortcutField::new();
        let mut label = WidgetLabel::new(name, Role::ShortcutField);
        widget.sync_label(&mut label);
        self.insert_widget(parent, label, Box::new(widget))
    }

    /// Set or clear a shortcut field's combo programmatically.
    pub fn set_shortcut_combo(
        &mut self,
        id: NodeId,
        combo: Option<crate::key_combo::KeyCombo>,
    ) {
        self.with_widget::<ShortcutField>(id, |sf, label| sf.set_combo(combo, label));
    }

    /// Convenience: a type-to-filter list.
    pub fn filter_listbox(
        &mut self,
        parent: Option<NodeId>,
        name: impl Into<String>,
        items: Vec<String>,
    ) -> NodeId {
        let widget = FilterListBox::new(items);
        let mut label = WidgetLabel::new(name, Role::ListBox);
        widget.sync_label(&mut label);
        self.insert_widget(parent, label, Box::new(widget))
    }

    /// Replace a filter list's items (filter kept, selection reset).
    /// Re-announces if focused and the label changed.
    pub fn set_filter_items(&mut self, id: NodeId, items: Vec<String>) {
        self.with_widget::<FilterListBox>(id, |fl, label| fl.set_items(items, label));
    }

    /// Clear a filter list's query and selection.
    pub fn clear_filter(&mut self, id: NodeId) {
        self.with_widget::<FilterListBox>(id, |fl, label| fl.clear_filter(label));
    }

    /// Convenience: a single-line edit box with initial text.
    pub fn editbox(
        &mut self,
        parent: Option<NodeId>,
        name: impl Into<String>,
        text: &str,
    ) -> NodeId {
        self.editbox_impl(parent, Some(name.into()), text, false)
    }

    /// Convenience: a multi-line edit box with initial text.
    pub fn editbox_multiline(
        &mut self,
        parent: Option<NodeId>,
        name: impl Into<String>,
        text: &str,
    ) -> NodeId {
        self.editbox_impl(parent, Some(name.into()), text, true)
    }

    fn editbox_impl(
        &mut self,
        parent: Option<NodeId>,
        name: Option<String>,
        text: &str,
        multiline: bool,
    ) -> NodeId {
        let widget = EditBox::new(text, multiline);
        let role = Role::EditBox {
            read_only: false,
            multiline,
        };
        let mut label = match name {
            Some(name) => WidgetLabel::new(name, role),
            None => WidgetLabel::nameless(role),
        };
        widget.sync_label(&mut label);
        self.insert_widget(parent, label, Box::new(widget))
    }

    /// Replace an editbox's content (cursor clamped, selection cleared).
    /// Re-announces if the editbox is focused and its label changed.
    pub fn set_editbox_text(&mut self, id: NodeId, text: &str) {
        self.with_widget::<EditBox>(id, |edit, label| edit.set_text(text, label));
    }

    /// Toggle an editbox's read-only state.
    pub fn set_editbox_read_only(&mut self, id: NodeId, read_only: bool) {
        self.with_widget::<EditBox>(id, |edit, label| edit.set_read_only(read_only, label));
    }

    /// Convenience: a group container.
    pub fn group(&mut self, parent: Option<NodeId>, name: impl Into<String>) -> NodeId {
        self.insert(parent, WidgetLabel::new(name, Role::Group))
    }

    /// Convenience: a static text label.
    pub fn text_label(&mut self, parent: Option<NodeId>, text: impl Into<String>) -> NodeId {
        self.insert(parent, WidgetLabel::new(text, Role::Label))
    }

    /// Remove a node and its subtree. If focus was inside the removed
    /// subtree, it recovers to the nearest surviving focusable node and
    /// the recovery is announced.
    pub fn remove(&mut self, id: NodeId) {
        let parent = self.tree.parent(id);
        let focus_inside = self
            .tree
            .focus()
            .map(|f| f == id || self.is_ancestor(id, f))
            .unwrap_or(false);

        self.tree.remove(id);
        self.focus_memory.gc(&self.tree);

        if focus_inside {
            if let Some(next) = nav::recover_focus(&self.tree, parent) {
                self.tree.set_focus(next);
                self.emit_focused(next);
            }
        }
    }

    fn is_ancestor(&self, ancestor: NodeId, mut node: NodeId) -> bool {
        while let Some(parent) = self.tree.parent(node) {
            if parent == ancestor {
                return true;
            }
            node = parent;
        }
        false
    }

    // ── Accessors ──

    pub fn tree(&self) -> &Tree {
        &self.tree
    }

    pub fn focus(&self) -> Option<NodeId> {
        self.tree.focus()
    }

    pub fn label(&self, id: NodeId) -> Option<&WidgetLabel> {
        self.tree.get(id).map(|n| &n.label)
    }

    /// Typed access to a node's widget state (e.g. `ui.widget::<CheckBox>(id)`).
    pub fn widget<T: Widget>(&self, id: NodeId) -> Option<&T> {
        self.widgets.get(id)?.as_any().downcast_ref()
    }

    /// Typed mutable access to a node's widget state.
    pub fn widget_mut<T: Widget>(&mut self, id: NodeId) -> Option<&mut T> {
        self.widgets.get_mut(id)?.as_any_mut().downcast_mut()
    }

    // ── Label mutation ──

    /// Mutate a node's label. If the node is focused and the label
    /// actually changed, the focus is re-announced (coalescing collapses
    /// bursts).
    pub fn update_label(&mut self, id: NodeId, f: impl FnOnce(&mut WidgetLabel)) {
        let Some(node) = self.tree.get_mut(id) else {
            return;
        };
        let before = node.label.clone();
        f(&mut node.label);
        if self.tree.focus() == Some(id) && self.tree.get(id).unwrap().label != before {
            self.emit_focused(id);
        }
    }

    // ── Focus ──

    /// Move focus programmatically. Announces the newly focused node.
    pub fn set_focus(&mut self, id: NodeId) {
        if !self.tree.contains(id) {
            return;
        }
        self.set_focus_internal(id);
    }

    /// If nothing is focused, focus the first focusable node and announce
    /// it. Hosts call this once after building the initial UI.
    pub fn ensure_focus(&mut self) -> bool {
        if self.tree.focus().is_some() {
            return false;
        }
        if let Some(first) = nav::tab_next(&self.tree, None) {
            self.tree.set_focus(first);
            self.emit_focused(first);
            return true;
        }
        false
    }

    fn set_focus_internal(&mut self, new: NodeId) {
        if let Some(old) = self.tree.focus() {
            if old == new {
                return;
            }
            // Remember the child we're leaving for container re-entry.
            if let Some(parent) = self.tree.parent(old) {
                self.focus_memory.remember(parent, old);
            }
        }
        self.tree.set_focus(new);
        self.emit_focused(new);
    }

    fn emit_focused(&mut self, id: NodeId) {
        if let Some(node) = self.tree.get(id) {
            self.events
                .push(OutputEvent::Accessibility(AccessibilityEvent::Focused {
                    node: id,
                    label: node.label.clone(),
                    context_labels: Vec::new(),
                }));
        }
    }

    /// Re-announce the focused node with its context labels (the names of
    /// Label-role siblings preceding it in child order). Hosts call this
    /// after a view transition, when the plain announcement would lack
    /// orientation.
    pub fn reannounce_with_context(&mut self) {
        let Some(id) = self.tree.focus() else {
            return;
        };
        let Some(node) = self.tree.get(id) else {
            return;
        };
        let label = node.label.clone();
        let context_labels = self.context_labels_for(id);
        self.events
            .push(OutputEvent::Accessibility(AccessibilityEvent::Focused {
                node: id,
                label,
                context_labels,
            }));
    }

    fn context_labels_for(&self, id: NodeId) -> Vec<String> {
        let siblings = match self.tree.parent(id) {
            Some(parent) => self.tree.children(parent),
            None => self.tree.roots(),
        };
        let mut out = Vec::new();
        for &sib in siblings {
            if sib == id {
                break;
            }
            if let Some(node) = self.tree.get(sib) {
                if node.label.role == Role::Label {
                    if let Some(name) = node.label.name_str() {
                        if !name.is_empty() {
                            out.push(name.to_string());
                        }
                    }
                }
            }
        }
        out
    }

    // ── Layers ──

    /// Set the active layer's primary widget (Enter activates it when the
    /// focused widget doesn't claim Enter).
    pub fn set_primary(&mut self, id: NodeId) {
        self.tree.set_primary(id);
    }

    /// Set the active layer's cancel widget (Escape activates it).
    pub fn set_cancel(&mut self, id: NodeId) {
        self.tree.set_cancel(id);
    }

    /// Push a modal layer. New root nodes go into it; only it is navigable.
    pub fn push_layer(&mut self) {
        self.tree.push_layer();
    }

    /// Pop the top layer. The previous layer's focus is restored and
    /// announced.
    pub fn pop_layer(&mut self) {
        let restored = self.tree.pop_layer();
        self.focus_memory.gc(&self.tree);
        if let Some(id) = restored {
            self.emit_focused(id);
        }
    }

    // ── Input dispatch ──

    /// Dispatch one logical input. Claim order: the focused node's widget
    /// first, then framework navigation. Returns true if the input was
    /// consumed; the host routes unconsumed input to its own bindings.
    pub fn handle_input(&mut self, input: &LogicalInput) -> bool {
        // Establish focus if the tree has focusable content but no focus.
        if self.tree.focus().is_none() {
            let established = self.ensure_focus();
            // A tab press that established focus is satisfied by it.
            if established
                && matches!(
                    input,
                    LogicalInput::NavigateNext | LogicalInput::NavigatePrev
                )
            {
                return true;
            }
        }

        // 1. Focused widget gets first claim.
        if let Some(focused) = self.tree.focus() {
            if let Some(widget) = self.widgets.get_mut(focused) {
                if let Some(node) = self.tree.get_mut(focused) {
                    let mut ctx = WidgetCtx {
                        node: focused,
                        label: &mut node.label,
                        events: &mut self.events,
                        now_ms: self.now_ms,
                        clipboard: self.clipboard.as_mut(),
                    };
                    if widget.handle_input(input, &mut ctx) {
                        return true;
                    }
                }
            }
        }

        // 2. Framework navigation and layer defaults.
        match input {
            LogicalInput::NavigateNext => {
                if let Some(next) = nav::tab_next(&self.tree, self.tree.focus()) {
                    self.set_focus_internal(next);
                }
                true
            }
            LogicalInput::NavigatePrev => {
                if let Some(prev) = nav::tab_prev(&self.tree, self.tree.focus()) {
                    self.set_focus_internal(prev);
                }
                true
            }
            LogicalInput::TreeUp => {
                self.tree_nav(TreeDirection::Up);
                true
            }
            LogicalInput::TreeDown => {
                self.tree_nav_down();
                true
            }
            LogicalInput::TreeLeft => {
                self.tree_nav(TreeDirection::Left);
                true
            }
            LogicalInput::TreeRight => {
                self.tree_nav(TreeDirection::Right);
                true
            }
            LogicalInput::Shortcut(ch) => {
                if let Some(target) = nav::find_shortcut(&self.tree, *ch) {
                    self.set_focus_internal(target);
                    return true;
                }
                false
            }
            LogicalInput::SpeakFocus => {
                if let Some(id) = self.tree.focus() {
                    self.emit_focused(id);
                }
                true
            }
            LogicalInput::Activate => {
                if let Some(primary) = self.tree.primary() {
                    self.events.push(OutputEvent::Widget(WidgetEvent::Activated {
                        node: primary,
                    }));
                    return true;
                }
                false
            }
            LogicalInput::Dismiss => {
                if let Some(cancel) = self.tree.cancel() {
                    self.events.push(OutputEvent::Widget(WidgetEvent::Activated {
                        node: cancel,
                    }));
                    return true;
                }
                false
            }
            _ => false,
        }
    }

    fn tree_nav(&mut self, direction: TreeDirection) {
        let Some(current) = self.tree.focus() else {
            return;
        };
        if let Some(target) = nav::tree_nav(&self.tree, current, direction) {
            self.set_focus_internal(target);
        }
    }

    /// Hierarchy-down with focus memory: re-entering a container returns
    /// to its last-focused child when that child still exists and is
    /// focusable; otherwise the first visible child.
    fn tree_nav_down(&mut self) {
        let Some(container) = self.tree.focus() else {
            return;
        };
        if let Some(remembered) = self.focus_memory.recall(container) {
            let valid = self.tree.parent(remembered) == Some(container)
                && self
                    .tree
                    .get(remembered)
                    .map(|n| {
                        is_focusable(n.label.role, n.label.states)
                            && !n.label.states.contains(States::HIDDEN)
                    })
                    .unwrap_or(false);
            if valid {
                self.set_focus_internal(remembered);
                return;
            }
        }
        if let Some(target) = nav::tree_nav(&self.tree, container, TreeDirection::Down) {
            self.set_focus_internal(target);
        }
    }

    // ── Output ──

    /// Queue a free-form announcement ("Nothing to delete", status
    /// messages) for the readers.
    pub fn announce(&mut self, text: impl Into<String>) {
        self.events
            .push(OutputEvent::Accessibility(AccessibilityEvent::Announce {
                text: text.into(),
            }));
    }

    /// Drain the output queue, applying coalescing rules (see
    /// `events::coalesce`).
    pub fn drain_events(&mut self) -> Vec<OutputEvent> {
        events::coalesce(std::mem::take(&mut self.events))
    }
}

impl Default for Ui {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::speech;

    /// Render every accessibility event in a drained batch, in order.
    fn spoken(events: &[OutputEvent]) -> Vec<String> {
        events
            .iter()
            .filter_map(|ev| match ev {
                OutputEvent::Accessibility(a) => speech::render_event(a),
                OutputEvent::Widget(_) | OutputEvent::Tick { .. } => None,
            })
            .collect()
    }

    fn demo_ui() -> (Ui, NodeId, NodeId, NodeId) {
        let mut ui = Ui::new();
        let save = ui.button(None, "Save");
        let options = ui.group(None, "Options");
        let wrap = ui.checkbox(Some(options), "Word Wrap", false);
        (ui, save, options, wrap)
    }

    #[test]
    fn ensure_focus_announces_first_focusable() {
        let (mut ui, save, ..) = demo_ui();
        assert!(ui.ensure_focus());
        assert_eq!(ui.focus(), Some(save));
        assert_eq!(spoken(&ui.drain_events()), vec!["Save button"]);
    }

    #[test]
    fn first_tab_establishes_focus_without_moving() {
        let (mut ui, save, ..) = demo_ui();
        assert!(ui.handle_input(&LogicalInput::NavigateNext));
        assert_eq!(ui.focus(), Some(save));
    }

    #[test]
    fn tab_moves_and_announces() {
        let (mut ui, _save, _options, wrap) = demo_ui();
        ui.ensure_focus();
        ui.drain_events();

        ui.handle_input(&LogicalInput::NavigateNext);
        assert_eq!(ui.focus(), Some(wrap));
        assert_eq!(
            spoken(&ui.drain_events()),
            vec!["Word Wrap check box not checked"]
        );
    }

    #[test]
    fn coalescing_keeps_only_settled_focus() {
        let (mut ui, save, _options, _wrap) = demo_ui();
        ui.ensure_focus();
        ui.handle_input(&LogicalInput::NavigateNext);
        ui.handle_input(&LogicalInput::NavigateNext); // wraps back to Save
        assert_eq!(ui.focus(), Some(save));
        // Three Focused events were emitted; one survives the drain.
        assert_eq!(spoken(&ui.drain_events()), vec!["Save button"]);
    }

    #[test]
    fn button_activates_on_enter_and_space() {
        let (mut ui, save, ..) = demo_ui();
        ui.set_focus(save);
        ui.drain_events();

        assert!(ui.handle_input(&LogicalInput::Activate));
        assert!(ui.handle_input(&LogicalInput::TypeChar(' ')));
        let activations: Vec<_> = ui
            .drain_events()
            .into_iter()
            .filter(|ev| matches!(ev, OutputEvent::Widget(WidgetEvent::Activated { node }) if *node == save))
            .collect();
        assert_eq!(activations.len(), 2);
    }

    #[test]
    fn checkbox_toggles_on_space() {
        let (mut ui, _save, _options, wrap) = demo_ui();
        ui.set_focus(wrap);
        ui.drain_events();

        ui.handle_input(&LogicalInput::TypeChar(' '));
        let events = ui.drain_events();
        assert!(events.contains(&OutputEvent::Widget(WidgetEvent::Toggled {
            node: wrap,
            checked: true
        })));
        assert_eq!(spoken(&events), vec!["checked"]);
        assert!(ui.widget::<CheckBox>(wrap).unwrap().checked);
        assert_eq!(ui.label(wrap).unwrap().value, "checked");

        ui.handle_input(&LogicalInput::TypeChar(' '));
        assert_eq!(spoken(&ui.drain_events()), vec!["not checked"]);
        assert!(!ui.widget::<CheckBox>(wrap).unwrap().checked);
    }

    #[test]
    fn enter_on_checkbox_falls_through_to_primary() {
        let mut ui = Ui::new();
        let wrap = ui.checkbox(None, "Word Wrap", false);
        let ok = ui.button(None, "OK");
        ui.set_primary(ok);
        ui.set_focus(wrap);
        ui.drain_events();

        assert!(ui.handle_input(&LogicalInput::Activate));
        let events = ui.drain_events();
        assert!(events.contains(&OutputEvent::Widget(WidgetEvent::Activated { node: ok })));
        // The checkbox itself did not toggle.
        assert!(!ui.widget::<CheckBox>(wrap).unwrap().checked);
    }

    #[test]
    fn dismiss_activates_cancel() {
        let mut ui = Ui::new();
        let _edit = ui.button(None, "Body");
        let cancel = ui.button(None, "Cancel");
        ui.set_cancel(cancel);
        ui.ensure_focus();
        ui.drain_events();

        assert!(ui.handle_input(&LogicalInput::Dismiss));
        assert!(ui
            .drain_events()
            .contains(&OutputEvent::Widget(WidgetEvent::Activated { node: cancel })));
    }

    #[test]
    fn dismiss_unconsumed_without_cancel() {
        let (mut ui, ..) = demo_ui();
        ui.ensure_focus();
        assert!(!ui.handle_input(&LogicalInput::Dismiss));
    }

    #[test]
    fn mnemonic_jumps_to_widget() {
        let (mut ui, _save, _options, wrap) = demo_ui();
        ui.update_label(wrap, |l| l.shortcut = Some('w'));
        ui.ensure_focus();
        ui.drain_events();

        assert!(ui.handle_input(&LogicalInput::Shortcut('w')));
        assert_eq!(ui.focus(), Some(wrap));
    }

    #[test]
    fn speak_focus_reannounces() {
        let (mut ui, ..) = demo_ui();
        ui.ensure_focus();
        ui.drain_events();

        ui.handle_input(&LogicalInput::SpeakFocus);
        assert_eq!(spoken(&ui.drain_events()), vec!["Save button"]);
    }

    #[test]
    fn focus_recovers_when_focused_node_removed() {
        let (mut ui, _save, _options, wrap) = demo_ui();
        ui.set_focus(wrap);
        ui.drain_events();

        ui.remove(wrap);
        assert!(ui.focus().is_some());
        assert_ne!(ui.focus(), Some(wrap));
        // Recovery is announced.
        assert_eq!(spoken(&ui.drain_events()).len(), 1);
    }

    #[test]
    fn focus_recovers_when_focused_subtree_removed() {
        let (mut ui, save, options, wrap) = demo_ui();
        ui.set_focus(wrap);
        ui.drain_events();

        ui.remove(options);
        assert_eq!(ui.focus(), Some(save));
    }

    #[test]
    fn focus_memory_restores_last_child_on_reentry() {
        let mut ui = Ui::new();
        let group = ui.group(None, "Options");
        let _first = ui.checkbox(Some(group), "First", false);
        let second = ui.checkbox(Some(group), "Second", false);

        ui.set_focus(second);
        // Leave the container upward, then re-enter downward.
        ui.handle_input(&LogicalInput::TreeUp);
        assert_eq!(ui.focus(), Some(group));
        ui.handle_input(&LogicalInput::TreeDown);
        assert_eq!(ui.focus(), Some(second));
    }

    #[test]
    fn layer_pop_restores_and_announces_focus() {
        let (mut ui, save, ..) = demo_ui();
        ui.set_focus(save);
        ui.drain_events();

        ui.push_layer();
        let confirm = ui.button(None, "Confirm");
        ui.set_focus(confirm);
        ui.drain_events();

        ui.pop_layer();
        assert_eq!(ui.focus(), Some(save));
        assert_eq!(spoken(&ui.drain_events()), vec!["Save button"]);
    }

    #[test]
    fn update_label_reannounces_focused_node_only() {
        let (mut ui, save, _options, wrap) = demo_ui();
        ui.set_focus(save);
        ui.drain_events();

        // Unfocused node: silent.
        ui.update_label(wrap, |l| l.description = "wraps long lines".into());
        assert!(ui.drain_events().is_empty());

        // Focused node: re-announced.
        ui.update_label(save, |l| l.description = "saves the file".into());
        assert_eq!(
            spoken(&ui.drain_events()),
            vec!["Save button saves the file"]
        );

        // No-op mutation: silent.
        ui.update_label(save, |_| {});
        assert!(ui.drain_events().is_empty());
    }

    #[test]
    fn reannounce_with_context_collects_preceding_labels() {
        let mut ui = Ui::new();
        ui.push_layer();
        let _prompt = ui.text_label(None, "Delete 3 files?");
        let yes = ui.button(None, "Yes");
        ui.set_focus(yes);
        ui.drain_events();

        ui.reannounce_with_context();
        assert_eq!(
            spoken(&ui.drain_events()),
            vec!["Delete 3 files? Yes button"]
        );
    }

    // ── ListBox ──

    fn list_ui(numbered: bool) -> (Ui, NodeId) {
        let mut ui = Ui::new();
        let files = ui.listbox(
            None,
            "Files",
            vec!["alpha.txt".into(), "bravo.txt".into(), "charlie.txt".into()],
            numbered,
        );
        ui.set_focus(files);
        ui.drain_events();
        (ui, files)
    }

    #[test]
    fn listbox_focus_announcement_includes_value_and_position() {
        let mut ui = Ui::new();
        let files = ui.listbox(
            None,
            "Files",
            vec!["alpha.txt".into(), "bravo.txt".into(), "charlie.txt".into()],
            true,
        );
        ui.set_focus(files);
        assert_eq!(
            spoken(&ui.drain_events()),
            vec!["Files list alpha.txt 1 of 3"]
        );
    }

    #[test]
    fn listbox_arrows_announce_items() {
        let (mut ui, files) = list_ui(true);

        assert!(ui.handle_input(&LogicalInput::MoveDown));
        let events = ui.drain_events();
        assert_eq!(spoken(&events), vec!["bravo.txt 2 of 3"]);
        assert!(events.contains(&OutputEvent::Widget(WidgetEvent::Changed { node: files })));
        assert_eq!(ui.widget::<ListBox>(files).unwrap().selected(), 1);
        assert_eq!(ui.label(files).unwrap().value, "bravo.txt");
    }

    #[test]
    fn listbox_unnumbered_announces_bare_item() {
        let (mut ui, _files) = list_ui(false);
        ui.handle_input(&LogicalInput::MoveDown);
        assert_eq!(spoken(&ui.drain_events()), vec!["bravo.txt"]);
    }

    #[test]
    fn listbox_boundaries_announce_without_moving() {
        let (mut ui, files) = list_ui(true);

        ui.handle_input(&LogicalInput::MoveUp);
        assert_eq!(spoken(&ui.drain_events()), vec!["top, alpha.txt 1 of 3"]);
        assert_eq!(ui.widget::<ListBox>(files).unwrap().selected(), 0);

        ui.handle_input(&LogicalInput::MoveToDocEnd);
        ui.drain_events();
        ui.handle_input(&LogicalInput::MoveDown);
        assert_eq!(
            spoken(&ui.drain_events()),
            vec!["bottom, charlie.txt 3 of 3"]
        );
    }

    #[test]
    fn listbox_home_end_jump() {
        let (mut ui, files) = list_ui(true);

        ui.handle_input(&LogicalInput::MoveToDocEnd);
        assert_eq!(ui.widget::<ListBox>(files).unwrap().selected(), 2);
        assert_eq!(spoken(&ui.drain_events()), vec!["charlie.txt 3 of 3"]);

        ui.handle_input(&LogicalInput::MoveToDocStart);
        assert_eq!(ui.widget::<ListBox>(files).unwrap().selected(), 0);
    }

    #[test]
    fn enter_in_list_activates_primary() {
        let (mut ui, files) = list_ui(true);
        let open = ui.button(None, "Open");
        ui.set_primary(open);
        ui.handle_input(&LogicalInput::MoveDown);
        ui.drain_events();

        // The list does not claim Enter; it routes to the primary, which
        // reads the selection.
        assert!(ui.handle_input(&LogicalInput::Activate));
        assert!(ui
            .drain_events()
            .contains(&OutputEvent::Widget(WidgetEvent::Activated { node: open })));
        assert_eq!(
            ui.widget::<ListBox>(files).unwrap().selected_item(),
            Some("bravo.txt")
        );
    }

    #[test]
    fn enter_in_list_unconsumed_without_primary() {
        let (mut ui, _files) = list_ui(true);
        assert!(!ui.handle_input(&LogicalInput::Activate));
        assert!(ui.drain_events().is_empty());
    }

    #[test]
    fn listbox_typeahead_first_letter_cycles() {
        let mut ui = Ui::new();
        let files = ui.listbox(
            None,
            "Files",
            vec!["apple".into(), "banana".into(), "avocado".into()],
            false,
        );
        ui.set_focus(files);
        ui.drain_events();

        // 'a' from apple → next item starting with 'a' (wraps past banana).
        ui.set_now(1000);
        ui.handle_input(&LogicalInput::TypeChar('a'));
        assert_eq!(ui.widget::<ListBox>(files).unwrap().selected_item(), Some("avocado"));
        assert_eq!(spoken(&ui.drain_events()), vec!["avocado"]);

        // Repeated 'a' cycles onward: avocado → apple.
        ui.set_now(1100);
        ui.handle_input(&LogicalInput::TypeChar('a'));
        assert_eq!(ui.widget::<ListBox>(files).unwrap().selected_item(), Some("apple"));
    }

    #[test]
    fn listbox_typeahead_prefix_search() {
        let mut ui = Ui::new();
        let files = ui.listbox(
            None,
            "Files",
            vec!["banana".into(), "berry".into(), "cherry".into()],
            false,
        );
        ui.set_focus(files);
        ui.drain_events();

        ui.set_now(1000);
        ui.handle_input(&LogicalInput::TypeChar('b'));
        // From banana, 'b' cycles to the NEXT b-item: berry.
        assert_eq!(ui.widget::<ListBox>(files).unwrap().selected_item(), Some("berry"));
        ui.set_now(1100);
        ui.handle_input(&LogicalInput::TypeChar('e'));
        // Buffer "be" → prefix search keeps berry.
        assert_eq!(ui.widget::<ListBox>(files).unwrap().selected_item(), Some("berry"));
    }

    #[test]
    fn listbox_typeahead_times_out() {
        let mut ui = Ui::new();
        let files = ui.listbox(
            None,
            "Files",
            vec!["banana".into(), "berry".into(), "cat".into()],
            false,
        );
        ui.set_focus(files);
        ui.drain_events();

        ui.set_now(1000);
        ui.handle_input(&LogicalInput::TypeChar('b'));
        assert_eq!(ui.widget::<ListBox>(files).unwrap().selected_item(), Some("berry"));

        // 500ms later the buffer has expired: 'c' is a fresh first letter,
        // not the prefix "bc".
        ui.set_now(1500);
        ui.handle_input(&LogicalInput::TypeChar('c'));
        assert_eq!(ui.widget::<ListBox>(files).unwrap().selected_item(), Some("cat"));
    }

    #[test]
    fn listbox_empty_consumes_nothing() {
        let mut ui = Ui::new();
        let files = ui.listbox(None, "Files", Vec::new(), true);
        ui.set_focus(files);
        assert_eq!(ui.label(files).unwrap().value, "empty");
        ui.drain_events();

        assert!(!ui.handle_input(&LogicalInput::Activate));
        assert!(ui.drain_events().is_empty());
    }

    #[test]
    fn set_list_items_clamps_and_reannounces_when_focused() {
        let (mut ui, files) = list_ui(true);
        ui.handle_input(&LogicalInput::MoveToDocEnd);
        ui.drain_events();

        ui.set_list_items(files, vec!["only.txt".into()]);
        assert_eq!(ui.widget::<ListBox>(files).unwrap().selected(), 0);
        assert_eq!(
            spoken(&ui.drain_events()),
            vec!["Files list only.txt 1 of 1"]
        );
    }

    #[test]
    fn set_list_items_silent_when_unfocused() {
        let mut ui = Ui::new();
        let files = ui.listbox(None, "Files", vec!["a".into()], false);
        let other = ui.button(None, "Other");
        ui.set_focus(other);
        ui.drain_events();

        ui.set_list_items(files, vec!["b".into()]);
        assert!(ui.drain_events().is_empty());
    }

    // ── EditBox ──

    #[test]
    fn editbox_focus_announcement() {
        let mut ui = Ui::new();
        let notes = ui.editbox(None, "Notes", "");
        ui.set_focus(notes);
        assert_eq!(spoken(&ui.drain_events()), vec!["Notes edit blank"]);
    }

    #[test]
    fn editbox_typing_echoes_and_updates_state() {
        let mut ui = Ui::new();
        let notes = ui.editbox(None, "Notes", "");
        ui.set_focus(notes);
        ui.drain_events();

        assert!(ui.handle_input(&LogicalInput::TypeChar('h')));
        let events = ui.drain_events();
        assert_eq!(spoken(&events), vec!["h"]);
        assert!(events.contains(&OutputEvent::Widget(WidgetEvent::Changed { node: notes })));

        ui.handle_input(&LogicalInput::TypeChar('i'));
        ui.drain_events();
        assert_eq!(ui.widget::<EditBox>(notes).unwrap().text(), "hi");
        assert_eq!(ui.label(notes).unwrap().value, "hi");
    }

    #[test]
    fn editbox_word_echo_on_boundary() {
        let mut ui = Ui::new();
        let notes = ui.editbox(None, "Notes", "");
        ui.set_focus(notes);
        for ch in ['h', 'e', 'y'] {
            ui.handle_input(&LogicalInput::TypeChar(ch));
        }
        ui.drain_events();
        ui.handle_input(&LogicalInput::TypeChar(' '));
        assert_eq!(spoken(&ui.drain_events()), vec!["hey space"]);
    }

    #[test]
    fn editbox_arrow_navigation_speaks_chars() {
        let mut ui = Ui::new();
        let notes = ui.editbox(None, "Notes", "ab");
        ui.set_focus(notes);
        ui.drain_events();

        // Cursor at 0; left is the top boundary.
        assert!(ui.handle_input(&LogicalInput::MoveLeft));
        assert_eq!(spoken(&ui.drain_events()), vec!["Top, a"]);

        assert!(ui.handle_input(&LogicalInput::MoveRight));
        assert_eq!(spoken(&ui.drain_events()), vec!["b"]);
    }

    #[test]
    fn editbox_enter_single_line_falls_through_to_primary() {
        let mut ui = Ui::new();
        let notes = ui.editbox(None, "Notes", "");
        let ok = ui.button(None, "OK");
        ui.set_primary(ok);
        ui.set_focus(notes);
        ui.drain_events();

        assert!(ui.handle_input(&LogicalInput::Activate));
        assert!(ui
            .drain_events()
            .contains(&OutputEvent::Widget(WidgetEvent::Activated { node: ok })));
        assert_eq!(ui.widget::<EditBox>(notes).unwrap().text(), "");
    }

    #[test]
    fn editbox_enter_multiline_inserts_newline() {
        let mut ui = Ui::new();
        let notes = ui.editbox_multiline(None, "Notes", "");
        ui.set_focus(notes);
        ui.drain_events();

        assert!(ui.handle_input(&LogicalInput::Activate));
        assert_eq!(spoken(&ui.drain_events()), vec!["new line"]);
        assert_eq!(ui.widget::<EditBox>(notes).unwrap().text(), "\n");
    }

    #[test]
    fn editbox_select_all_and_copy() {
        struct MemClipboard(Option<String>);
        impl crate::clipboard::Clipboard for MemClipboard {
            fn read(&mut self) -> Option<String> {
                self.0.clone()
            }
            fn write(&mut self, text: &str) {
                self.0 = Some(text.to_string());
            }
        }

        let mut ui = Ui::new();
        ui.set_clipboard(Box::new(MemClipboard(None)));
        let notes = ui.editbox(None, "Notes", "hello");
        ui.set_focus(notes);
        ui.drain_events();

        ui.handle_input(&LogicalInput::SelectAll);
        assert_eq!(spoken(&ui.drain_events()), vec!["hello selected"]);

        ui.handle_input(&LogicalInput::Copy);
        assert_eq!(spoken(&ui.drain_events()), vec!["Copy"]);

        // Paste at the end doubles the text via the injected clipboard.
        ui.handle_input(&LogicalInput::MoveToDocEnd);
        ui.handle_input(&LogicalInput::Paste);
        ui.drain_events();
        assert_eq!(ui.widget::<EditBox>(notes).unwrap().text(), "hellohello");
    }

    #[test]
    fn set_editbox_text_reannounces_when_focused() {
        let mut ui = Ui::new();
        let notes = ui.editbox(None, "Notes", "old");
        ui.set_focus(notes);
        ui.drain_events();

        ui.set_editbox_text(notes, "new text");
        assert_eq!(spoken(&ui.drain_events()), vec!["Notes edit new text"]);
        assert_eq!(ui.widget::<EditBox>(notes).unwrap().text(), "new text");
    }

    // ── Slider ──

    #[test]
    fn slider_adjusts_and_announces() {
        let mut ui = Ui::new();
        let vol = ui.slider_widget(
            None,
            "Volume",
            crate::widget::Slider::new(50, 0, 100).with_unit("%"),
        );
        ui.set_focus(vol);
        assert_eq!(spoken(&ui.drain_events()), vec!["Volume slider 50%"]);

        assert!(ui.handle_input(&LogicalInput::MoveRight));
        let events = ui.drain_events();
        assert_eq!(spoken(&events), vec!["51%"]);
        assert!(events.contains(&OutputEvent::Widget(WidgetEvent::Changed { node: vol })));

        // Large steps via Shift+arrow and PageDown.
        ui.handle_input(&LogicalInput::SelectRight);
        assert_eq!(spoken(&ui.drain_events()), vec!["61%"]);
        ui.handle_input(&LogicalInput::RawKey(crate::key_combo::KeyCombo::plain(
            crate::key_combo::Key::PageDown,
        )));
        assert_eq!(spoken(&ui.drain_events()), vec!["51%"]);

        // Home/End jump to the edges.
        ui.handle_input(&LogicalInput::MoveToLineEnd);
        assert_eq!(spoken(&ui.drain_events()), vec!["100%"]);
        assert_eq!(ui.widget::<Slider>(vol).unwrap().value(), 100);

        // Clamped at max: consumed, re-announced, but no Changed event.
        ui.handle_input(&LogicalInput::MoveRight);
        let events = ui.drain_events();
        assert_eq!(spoken(&events), vec!["100%"]);
        assert!(!events
            .iter()
            .any(|e| matches!(e, OutputEvent::Widget(WidgetEvent::Changed { .. }))));
    }

    // ── TabControl ──

    #[test]
    fn tab_control_cycles_with_wraparound() {
        let mut ui = Ui::new();
        let tabs = ui.tab_control(
            None,
            "Views",
            vec!["Files".into(), "Playlist".into(), "FX".into()],
            0,
        );
        ui.set_focus(tabs);
        assert_eq!(spoken(&ui.drain_events()), vec!["Views tab control Files"]);

        ui.handle_input(&LogicalInput::MoveRight);
        assert_eq!(spoken(&ui.drain_events()), vec!["Playlist"]);
        ui.handle_input(&LogicalInput::MoveRight);
        assert_eq!(spoken(&ui.drain_events()), vec!["FX"]);
        ui.handle_input(&LogicalInput::MoveRight); // wraps
        assert_eq!(spoken(&ui.drain_events()), vec!["Files"]);

        ui.handle_input(&LogicalInput::MoveLeft); // wraps backward
        assert_eq!(spoken(&ui.drain_events()), vec!["FX"]);
        assert_eq!(ui.widget::<TabControl>(tabs).unwrap().active(), 2);
        assert_eq!(ui.label(tabs).unwrap().value, "FX");
    }

    // ── ShortcutField ──

    #[test]
    fn shortcut_field_captures_and_clears() {
        use crate::key_combo::{Key, KeyCombo};

        let mut ui = Ui::new();
        let field = ui.shortcut_field(None, "Play shortcut");
        ui.set_focus(field);
        assert_eq!(
            spoken(&ui.drain_events()),
            vec!["Play shortcut shortcut field blank"]
        );

        // A raw combo is captured verbatim.
        let ctrl_s = KeyCombo::ctrl(Key::Char('s'));
        assert!(ui.handle_input(&LogicalInput::RawKey(ctrl_s)));
        let events = ui.drain_events();
        assert_eq!(spoken(&events), vec!["control s"]);
        assert!(events.contains(&OutputEvent::Widget(WidgetEvent::Changed { node: field })));
        assert_eq!(ui.widget::<ShortcutField>(field).unwrap().combo(), Some(ctrl_s));

        // A semantically-mapped input is captured via its combo.
        assert!(ui.handle_input(&LogicalInput::Copy));
        assert_eq!(spoken(&ui.drain_events()), vec!["control c"]);

        // Backspace clears.
        assert!(ui.handle_input(&LogicalInput::DeleteBackward));
        assert_eq!(spoken(&ui.drain_events()), vec!["blank"]);
        assert_eq!(ui.widget::<ShortcutField>(field).unwrap().combo(), None);

        // Tab still leaves the field.
        assert!(ui.handle_input(&LogicalInput::NavigateNext));
    }

    #[test]
    fn shortcut_field_resists_framework_interception() {
        use crate::key_combo::{Key, KeyCombo};

        let mut ui = Ui::new();
        let group = ui.group(None, "Options");
        let field = ui.shortcut_field(Some(group), "Shortcut");
        let other = ui.button(None, "Other");
        ui.update_label(other, |l| l.shortcut = Some('o'));
        ui.set_focus(field);
        ui.drain_events();

        // Alt+Up would be hierarchy navigation; the field captures it.
        ui.handle_input(&LogicalInput::TreeUp);
        assert_eq!(ui.focus(), Some(field));
        assert_eq!(
            ui.widget::<ShortcutField>(field).unwrap().combo(),
            Some(KeyCombo::alt(Key::Up))
        );

        // Alt+O would be a mnemonic jump; the field captures it.
        ui.handle_input(&LogicalInput::Shortcut('o'));
        assert_eq!(ui.focus(), Some(field));
        assert_eq!(
            ui.widget::<ShortcutField>(field).unwrap().combo(),
            Some(KeyCombo::alt(Key::Char('o')))
        );

        // Ctrl+Tab arrives as RawKey and is captured (it is host-bindable).
        ui.handle_input(&LogicalInput::RawKey(KeyCombo::ctrl(Key::Tab)));
        assert_eq!(
            ui.widget::<ShortcutField>(field).unwrap().combo(),
            Some(KeyCombo::ctrl(Key::Tab))
        );

        // Escape still dismisses (unconsumed here — no cancel widget).
        assert!(!ui.handle_input(&LogicalInput::Dismiss));
        assert_eq!(
            ui.widget::<ShortcutField>(field).unwrap().combo(),
            Some(KeyCombo::ctrl(Key::Tab))
        );
    }

    // ── FilterListBox ──

    fn filter_ui() -> (Ui, NodeId) {
        let mut ui = Ui::new();
        let list = ui.filter_listbox(
            None,
            "Commands",
            vec![
                "Save File".into(),
                "Open Editor".into(),
                "Quit".into(),
            ],
        );
        ui.set_focus(list);
        ui.drain_events();
        (ui, list)
    }

    #[test]
    fn filter_listbox_focus_announcement_carries_filter_state() {
        let mut ui = Ui::new();
        let list = ui.filter_listbox(None, "Commands", vec!["Save File".into()]);
        ui.set_focus(list);
        assert_eq!(
            spoken(&ui.drain_events()),
            vec!["Commands list Save File no filter"]
        );
    }

    #[test]
    fn filter_listbox_typing_filters_and_reports() {
        let mut ui = Ui::new();
        let list = ui.filter_listbox(
            None,
            "Commands",
            vec![
                "Save File".into(),
                "Settings".into(),
                "Open Editor".into(),
                "Quit".into(),
            ],
        );
        ui.set_focus(list);
        ui.drain_events();

        ui.handle_input(&LogicalInput::TypeChar('s'));
        let events = ui.drain_events();
        // "s" matches Save File best.
        assert_eq!(spoken(&events), vec!["Save File 1 of 2"]);
        assert!(events.contains(&OutputEvent::Widget(WidgetEvent::Changed { node: list })));
        assert_eq!(ui.label(list).unwrap().state_text, "filter s");

        ui.handle_input(&LogicalInput::TypeChar('x'));
        assert_eq!(spoken(&ui.drain_events()), vec!["no results"]);
        assert_eq!(ui.label(list).unwrap().value, "empty");

        // Backspace restores results.
        ui.handle_input(&LogicalInput::DeleteBackward);
        assert_eq!(spoken(&ui.drain_events()), vec!["Save File 1 of 2"]);
    }

    #[test]
    fn filter_listbox_arrows_navigate_filtered_set() {
        let (mut ui, list) = filter_ui();
        ui.handle_input(&LogicalInput::TypeChar('e'));
        ui.drain_events();

        ui.handle_input(&LogicalInput::MoveDown);
        let spoken_items = spoken(&ui.drain_events());
        assert_eq!(spoken_items.len(), 1);
        assert!(spoken_items[0].ends_with("2 of 2"), "{spoken_items:?}");

        // Bottom boundary repeats with prefix.
        ui.handle_input(&LogicalInput::MoveDown);
        assert!(spoken(&ui.drain_events())[0].starts_with("bottom, "));

        let selected = ui.widget::<FilterListBox>(list).unwrap().selected_item();
        assert!(selected.is_some());
    }

    #[test]
    fn filter_listbox_enter_falls_through_to_primary() {
        let (mut ui, list) = filter_ui();
        let open = ui.button(None, "Open");
        ui.set_primary(open);
        ui.handle_input(&LogicalInput::TypeChar('q'));
        ui.drain_events();

        assert!(ui.handle_input(&LogicalInput::Activate));
        assert!(ui
            .drain_events()
            .contains(&OutputEvent::Widget(WidgetEvent::Activated { node: open })));
        assert_eq!(
            ui.widget::<FilterListBox>(list).unwrap().selected_item(),
            Some("Quit".to_string())
        );
    }

    // ── Tickers ──

    #[test]
    fn ticker_fires_on_clock_advance() {
        let mut ui = Ui::new();
        let ticker = ui.add_ticker(100);

        ui.set_now(50);
        assert!(ui.drain_events().is_empty());

        ui.set_now(100);
        assert_eq!(ui.drain_events(), vec![OutputEvent::Tick { ticker }]);

        // A long gap fires once, not once per missed interval.
        ui.set_now(950);
        assert_eq!(ui.drain_events(), vec![OutputEvent::Tick { ticker }]);
        // Next interval starts from the late check.
        ui.set_now(1000);
        assert!(ui.drain_events().is_empty());
        ui.set_now(1050);
        assert_eq!(ui.drain_events(), vec![OutputEvent::Tick { ticker }]);
    }

    #[test]
    fn removed_ticker_stops_firing() {
        let mut ui = Ui::new();
        let a = ui.add_ticker(10);
        let b = ui.add_ticker(10);
        ui.remove_ticker(a);
        ui.set_now(20);
        assert_eq!(ui.drain_events(), vec![OutputEvent::Tick { ticker: b }]);
    }

    #[test]
    fn unhandled_input_is_unconsumed() {
        let (mut ui, ..) = demo_ui();
        ui.ensure_focus();
        // A raw key nothing claims falls through to the host.
        let raw = LogicalInput::RawKey(crate::key_combo::KeyCombo::ctrl(
            crate::key_combo::Key::Char('s'),
        ));
        assert!(!ui.handle_input(&raw));
    }
}
