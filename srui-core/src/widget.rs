//! Widget behavior — trait-dispatched input handling per node.
//!
//! A `Widget` owns a node's interaction state and handles logical input
//! directed at the node while it is focused. Built-in behaviors live
//! here; custom widgets are Rust-only (bindings compose primitives and
//! subclass their wrapper classes instead — see docs/architecture.md,
//! section 5).

use std::any::Any;

use crate::clipboard::Clipboard;
use crate::editbox::{editbox_label_value, handle_editbox};
use crate::editor::EditorState;
use crate::events::{AccessibilityEvent, Boundary, OutputEvent, WidgetEvent};
use crate::filter::filter_items;
use crate::input::LogicalInput;
use crate::key_combo::{Key, KeyCombo};
use crate::tree::NodeId;
use crate::types::WidgetLabel;

/// Timeout for resetting the typeahead buffer (milliseconds of host time).
const TYPE_AHEAD_TIMEOUT_MS: u64 = 400;

/// Everything a widget may touch while handling input: its own node id,
/// its label (kept in sync with widget state), the output queue, the
/// host-supplied monotonic clock (see `Ui::set_now`), and the injected
/// clipboard.
pub struct WidgetCtx<'a> {
    pub node: NodeId,
    pub label: &'a mut WidgetLabel,
    pub events: &'a mut Vec<OutputEvent>,
    pub now_ms: u64,
    pub clipboard: &'a mut dyn Clipboard,
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

/// EditBox — a single- or multi-line text editor with full cursor
/// navigation, selection, clipboard, and typing echo. Enter inserts a
/// newline in multiline editors and falls through to the layer's primary
/// widget in single-line ones.
#[derive(Debug)]
pub struct EditBox {
    editor: EditorState,
}

impl EditBox {
    pub fn new(text: &str, multiline: bool) -> Self {
        Self {
            editor: EditorState::new(text, multiline),
        }
    }

    pub fn editor(&self) -> &EditorState {
        &self.editor
    }

    pub fn text(&self) -> String {
        self.editor.text()
    }

    /// Replace the content (cursor clamped, selection cleared).
    pub(crate) fn set_text(&mut self, text: &str, label: &mut WidgetLabel) {
        self.editor.set_text(text);
        self.sync_label(label);
    }

    pub(crate) fn set_read_only(&mut self, read_only: bool, label: &mut WidgetLabel) {
        self.editor.read_only = read_only;
        if let crate::types::Role::EditBox { read_only: ro, .. } = &mut label.role {
            *ro = read_only;
        }
        self.sync_label(label);
    }

    /// Label value mirrors the selection or the current line at cursor.
    pub(crate) fn sync_label(&self, label: &mut WidgetLabel) {
        label.value = editbox_label_value(&self.editor);
    }
}

impl Widget for EditBox {
    fn handle_input(&mut self, input: &LogicalInput, ctx: &mut WidgetCtx) -> bool {
        let result = handle_editbox(ctx.node, input, &mut self.editor, ctx.clipboard);
        if !result.consumed {
            return false;
        }
        for event in result.events {
            ctx.emit_accessibility(event);
        }
        if result.changed {
            ctx.emit_widget(WidgetEvent::Changed { node: ctx.node });
        }
        self.sync_label(ctx.label);
        true
    }

    fn as_any(&self) -> &dyn Any {
        self
    }
    fn as_any_mut(&mut self) -> &mut dyn Any {
        self
    }
}

/// Slider — arrows adjust by the small step, Shift+arrows and
/// PageUp/PageDown by the large step, Home/End jump to the range edges.
/// Adjustments at a range edge re-announce the clamped value.
#[derive(Debug)]
pub struct Slider {
    value: i32,
    min: i32,
    max: i32,
    small_step: i32,
    large_step: i32,
    /// Spoken and displayed immediately after the value ("%" → "50%").
    unit: String,
}

impl Slider {
    pub fn new(value: i32, min: i32, max: i32) -> Self {
        Self {
            value: value.clamp(min, max),
            min,
            max,
            small_step: 1,
            large_step: 10,
            unit: String::new(),
        }
    }

    pub fn with_steps(mut self, small: i32, large: i32) -> Self {
        self.small_step = small;
        self.large_step = large;
        self
    }

    pub fn with_unit(mut self, unit: impl Into<String>) -> Self {
        self.unit = unit.into();
        self
    }

    pub fn value(&self) -> i32 {
        self.value
    }

    pub(crate) fn set_value(&mut self, value: i32, label: &mut WidgetLabel) {
        self.value = value.clamp(self.min, self.max);
        self.sync_label(label);
    }

    pub(crate) fn sync_label(&self, label: &mut WidgetLabel) {
        label.value = format!("{}{}", self.value, self.unit);
    }

    /// The value-change announcement, shared between user-driven
    /// adjustment and programmatic `Ui::set_slider_value`.
    pub(crate) fn change_event(&self, node: NodeId) -> AccessibilityEvent {
        AccessibilityEvent::SliderChange {
            node,
            value: self.value,
            unit: self.unit.clone(),
        }
    }

    fn adjust(&mut self, delta: i32, ctx: &mut WidgetCtx) {
        self.value = (self.value + delta).clamp(self.min, self.max);
        self.announce(ctx);
    }

    fn announce(&self, ctx: &mut WidgetCtx) {
        let event = self.change_event(ctx.node);
        ctx.emit_accessibility(event);
    }
}

impl Widget for Slider {
    fn handle_input(&mut self, input: &LogicalInput, ctx: &mut WidgetCtx) -> bool {
        let prev = self.value;
        let delta: Option<i32> = match input {
            LogicalInput::MoveRight | LogicalInput::MoveUp => Some(self.small_step),
            LogicalInput::MoveLeft | LogicalInput::MoveDown => Some(-self.small_step),
            LogicalInput::SelectRight | LogicalInput::SelectLineUp => Some(self.large_step),
            LogicalInput::SelectLeft | LogicalInput::SelectLineDown => Some(-self.large_step),
            LogicalInput::MoveToLineStart => {
                self.value = self.min;
                self.announce(ctx);
                None
            }
            LogicalInput::MoveToLineEnd => {
                self.value = self.max;
                self.announce(ctx);
                None
            }
            LogicalInput::RawKey(combo) if !combo.ctrl && !combo.alt => match combo.key {
                Key::PageUp => Some(self.large_step),
                Key::PageDown => Some(-self.large_step),
                _ => return false,
            },
            _ => return false,
        };
        if let Some(d) = delta {
            self.adjust(d, ctx);
        }
        self.sync_label(ctx.label);
        if self.value != prev {
            ctx.emit_widget(WidgetEvent::Changed { node: ctx.node });
        }
        true
    }

    fn as_any(&self) -> &dyn Any {
        self
    }
    fn as_any_mut(&mut self) -> &mut dyn Any {
        self
    }
}

/// TabControl — Left/Right cycle through tabs with wraparound.
#[derive(Debug)]
pub struct TabControl {
    tabs: Vec<String>,
    active: usize,
}

impl TabControl {
    pub fn new(tabs: Vec<String>, active: usize) -> Self {
        let active = if tabs.is_empty() {
            0
        } else {
            active.min(tabs.len() - 1)
        };
        Self { tabs, active }
    }

    pub fn active(&self) -> usize {
        self.active
    }

    pub fn active_tab(&self) -> Option<&str> {
        self.tabs.get(self.active).map(|s| s.as_str())
    }

    pub(crate) fn set_active(&mut self, index: usize, label: &mut WidgetLabel) {
        if self.tabs.is_empty() {
            return;
        }
        self.active = index.min(self.tabs.len() - 1);
        self.sync_label(label);
    }

    pub(crate) fn sync_label(&self, label: &mut WidgetLabel) {
        if let Some(name) = self.tabs.get(self.active) {
            label.value = name.clone();
        }
    }

    /// The tab-change announcement, shared between user-driven switching
    /// and programmatic `Ui::set_active_tab`. `None` when there are no tabs.
    pub(crate) fn change_event(&self, node: NodeId) -> Option<AccessibilityEvent> {
        Some(AccessibilityEvent::TabChange {
            node,
            tab_name: self.tabs.get(self.active)?.clone(),
            position: (self.active, self.tabs.len()),
        })
    }

    fn switch(&mut self, to: usize, ctx: &mut WidgetCtx) {
        self.active = to;
        self.sync_label(ctx.label);
        if let Some(event) = self.change_event(ctx.node) {
            ctx.emit_accessibility(event);
        }
        ctx.emit_widget(WidgetEvent::Changed { node: ctx.node });
    }
}

impl Widget for TabControl {
    fn handle_input(&mut self, input: &LogicalInput, ctx: &mut WidgetCtx) -> bool {
        if self.tabs.is_empty() {
            return false;
        }
        match input {
            LogicalInput::MoveRight => {
                let next = if self.active + 1 < self.tabs.len() {
                    self.active + 1
                } else {
                    0
                };
                self.switch(next, ctx);
                true
            }
            LogicalInput::MoveLeft => {
                let prev = if self.active > 0 {
                    self.active - 1
                } else {
                    self.tabs.len() - 1
                };
                self.switch(prev, ctx);
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

/// ShortcutField — captures whatever combo the user presses as its value.
/// Delete/Backspace clear it; Tab, Escape, and SpeakFocus pass through so
/// the user can still leave the field.
#[derive(Debug, Default)]
pub struct ShortcutField {
    combo: Option<KeyCombo>,
    /// When false, capturing a combo produces no speech feedback.
    pub echo: bool,
}

impl ShortcutField {
    pub fn new() -> Self {
        Self {
            combo: None,
            echo: true,
        }
    }

    pub fn combo(&self) -> Option<KeyCombo> {
        self.combo
    }

    pub(crate) fn set_combo(&mut self, combo: Option<KeyCombo>, label: &mut WidgetLabel) {
        self.combo = combo;
        self.sync_label(label);
    }

    pub(crate) fn sync_label(&self, label: &mut WidgetLabel) {
        label.value = match &self.combo {
            Some(combo) => combo.display_name(),
            None => "blank".to_string(),
        };
    }

    fn capture(&mut self, combo: KeyCombo, ctx: &mut WidgetCtx) {
        self.combo = Some(combo);
        self.sync_label(ctx.label);
        if self.echo {
            self.say_value(combo.display_name(), ctx);
        }
        ctx.emit_widget(WidgetEvent::Changed { node: ctx.node });
    }

    /// A shortcut field has no indexable concept, so its value changes
    /// ride ItemNav with no position.
    fn say_value(&self, value: String, ctx: &mut WidgetCtx) {
        ctx.emit_accessibility(AccessibilityEvent::ItemNav {
            node: ctx.node,
            item: value,
            position: None,
            boundary: None,
        });
    }
}

impl Widget for ShortcutField {
    fn handle_input(&mut self, input: &LogicalInput, ctx: &mut WidgetCtx) -> bool {
        match input {
            // Delete/Backspace clears the shortcut
            LogicalInput::DeleteBackward | LogicalInput::DeleteForward => {
                if self.combo.is_some() {
                    self.combo = None;
                    self.sync_label(ctx.label);
                    self.say_value("blank".to_string(), ctx);
                    ctx.emit_widget(WidgetEvent::Changed { node: ctx.node });
                }
                true
            }

            // RawKey — capture the combo directly
            LogicalInput::RawKey(combo) => {
                self.capture(*combo, ctx);
                true
            }

            // Let Tab, Escape, and framework inputs through
            LogicalInput::NavigateNext
            | LogicalInput::NavigatePrev
            | LogicalInput::Dismiss
            | LogicalInput::SpeakFocus => false,

            // Any other input with a KeyCombo mapping — capture it
            other => {
                if let Some(combo) = KeyCombo::from_logical(other) {
                    if matches!(combo.key, Key::Tab | Key::Escape) {
                        // Let through for navigation/dismiss
                        false
                    } else {
                        self.capture(combo, ctx);
                        true
                    }
                } else {
                    // Unknown input — consume silently
                    true
                }
            }
        }
    }

    fn as_any(&self) -> &dyn Any {
        self
    }
    fn as_any_mut(&mut self) -> &mut dyn Any {
        self
    }
}

/// FilterListBox — type-to-filter list. Printable characters build a
/// fuzzy-match query, Backspace erases it, arrows and Home/End navigate
/// the filtered results. Enter is not claimed (the layer's primary reads
/// the selection).
#[derive(Debug, Default)]
pub struct FilterListBox {
    items: Vec<String>,
    filter: String,
    selected: usize,
}

impl FilterListBox {
    pub fn new(items: Vec<String>) -> Self {
        Self {
            items,
            filter: String::new(),
            selected: 0,
        }
    }

    pub fn filter(&self) -> &str {
        &self.filter
    }

    /// The items currently matching the filter, best match first.
    pub fn filtered(&self) -> Vec<String> {
        filter_items(&self.filter, &self.items)
    }

    pub fn selected_item(&self) -> Option<String> {
        self.filtered().into_iter().nth(self.selected)
    }

    /// Replace the full item list; the filter is kept, the selection reset.
    pub(crate) fn set_items(&mut self, items: Vec<String>, label: &mut WidgetLabel) {
        self.items = items;
        self.selected = 0;
        self.sync_label(label);
    }

    /// Clear the filter and selection.
    pub(crate) fn clear_filter(&mut self, label: &mut WidgetLabel) {
        self.filter.clear();
        self.selected = 0;
        self.sync_label(label);
    }

    /// Label value mirrors the selected result; state_text carries the
    /// filter ("no filter" / "filter {query}").
    pub(crate) fn sync_label(&self, label: &mut WidgetLabel) {
        let filtered = self.filtered();
        match filtered.get(self.selected) {
            Some(item) => label.value = item.clone(),
            None => label.value = "empty".to_string(),
        }
        label.state_text = if self.filter.is_empty() {
            "no filter".to_string()
        } else {
            format!("filter {}", self.filter)
        };
    }

    fn emit_item(&self, filtered: &[String], ctx: &mut WidgetCtx, boundary: Option<Boundary>) {
        ctx.emit_accessibility(AccessibilityEvent::ItemNav {
            node: ctx.node,
            item: filtered[self.selected].clone(),
            position: Some((self.selected, filtered.len())),
            boundary,
        });
    }

    fn select_and_announce(&mut self, filtered: &[String], index: usize, ctx: &mut WidgetCtx) {
        self.selected = index;
        self.sync_label(ctx.label);
        self.emit_item(filtered, ctx, None);
        ctx.emit_widget(WidgetEvent::Changed { node: ctx.node });
    }

    /// Filter text changed: reset the selection and report the new results.
    fn filter_changed(&mut self, ctx: &mut WidgetCtx) {
        self.selected = 0;
        self.sync_label(ctx.label);
        let filtered = self.filtered();
        ctx.emit_accessibility(AccessibilityEvent::Filter {
            node: ctx.node,
            query: self.filter.clone(),
            first_result: filtered.first().cloned(),
            result_count: filtered.len(),
        });
        ctx.emit_widget(WidgetEvent::Changed { node: ctx.node });
    }
}

impl Widget for FilterListBox {
    fn handle_input(&mut self, input: &LogicalInput, ctx: &mut WidgetCtx) -> bool {
        let filtered = self.filtered();
        match input {
            LogicalInput::MoveDown if !filtered.is_empty() => {
                if self.selected + 1 < filtered.len() {
                    self.select_and_announce(&filtered, self.selected + 1, ctx);
                } else {
                    self.emit_item(&filtered, ctx, Some(Boundary::Bottom));
                }
                true
            }
            LogicalInput::MoveUp if !filtered.is_empty() => {
                if self.selected > 0 {
                    self.select_and_announce(&filtered, self.selected - 1, ctx);
                } else {
                    self.emit_item(&filtered, ctx, Some(Boundary::Top));
                }
                true
            }
            LogicalInput::MoveToDocStart | LogicalInput::MoveToLineStart
                if !filtered.is_empty() =>
            {
                if self.selected != 0 {
                    self.select_and_announce(&filtered, 0, ctx);
                }
                true
            }
            LogicalInput::MoveToDocEnd | LogicalInput::MoveToLineEnd if !filtered.is_empty() => {
                let last = filtered.len() - 1;
                if self.selected != last {
                    self.select_and_announce(&filtered, last, ctx);
                }
                true
            }
            LogicalInput::TypeChar(ch) => {
                self.filter.push(ch.to_ascii_lowercase());
                self.filter_changed(ctx);
                true
            }
            LogicalInput::DeleteBackward if !self.filter.is_empty() => {
                self.filter.pop();
                self.filter_changed(ctx);
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

    /// The selection announcement, shared between user-driven navigation
    /// and programmatic `Ui::set_list_selected`. `None` when empty.
    pub(crate) fn change_event(
        &self,
        node: NodeId,
        boundary: Option<Boundary>,
    ) -> Option<AccessibilityEvent> {
        let item = self.items.get(self.selected)?.clone();
        let position = if self.numbered {
            Some((self.selected, self.items.len()))
        } else {
            None
        };
        Some(AccessibilityEvent::ItemNav {
            node,
            item,
            position,
            boundary,
        })
    }

    /// Emit an ItemNav for the current selection.
    fn emit_item(&self, ctx: &mut WidgetCtx, boundary: Option<Boundary>) {
        if let Some(event) = self.change_event(ctx.node, boundary) {
            ctx.emit_accessibility(event);
        }
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
