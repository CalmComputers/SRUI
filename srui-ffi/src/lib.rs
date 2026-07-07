//! C ABI over srui-core plus the SDL host and Prism speech, so a binding
//! needs exactly one native surface (srui_ffi.dll, with prism.dll and
//! SDL3.dll beside it).
//!
//! Conventions (docs/architecture.md section 10): objects are opaque
//! pointers; nodes are the u64 form of NodeId with 0 meaning "no node";
//! all strings are UTF-8, and strings returned by this library are freed
//! with `srui_string_free`. There are no callbacks — hosts poll. Nothing
//! here is thread-safe: one context, one thread.
//!
//! Inputs and events cross as flat structs; the encodings are documented
//! on `SruiInputKind` values below and mirrored in Srui.Net.

use std::ffi::{c_char, CStr, CString};

use srui_core::events::{AccessibilityEvent, OutputEvent, WidgetEvent};
use srui_core::input::LogicalInput;
use srui_core::key_combo::{Key, KeyCombo};
use srui_core::speech::render_event;
use srui_core::tree::NodeId;
use srui_core::types::ShortcutAction;
use srui_core::ui::Ui;
use srui_core::widget::{CheckBox, EditBox, FilterListBox, ListBox, ShortcutField, Slider, TabControl};
use srui_prism::Speech;
use srui_sdl::{HostEvent, SdlHost};

// ── Helpers ──

fn node_in(raw: u64) -> Option<NodeId> {
    if raw == 0 {
        None
    } else {
        Some(NodeId::from_ffi(raw))
    }
}

unsafe fn str_in<'a>(ptr: *const c_char) -> &'a str {
    if ptr.is_null() {
        ""
    } else {
        CStr::from_ptr(ptr).to_str().unwrap_or("")
    }
}

fn str_out(s: String) -> *mut c_char {
    CString::new(s.replace('\0', " "))
        .expect("interior NULs replaced")
        .into_raw()
}

unsafe fn strings_in(ptr: *const *const c_char, len: usize) -> Vec<String> {
    if ptr.is_null() {
        return Vec::new();
    }
    (0..len)
        .map(|i| str_in(*ptr.add(i)).to_string())
        .collect()
}

/// Free a string returned by this library.
#[no_mangle]
pub unsafe extern "C" fn srui_string_free(s: *mut c_char) {
    if !s.is_null() {
        drop(CString::from_raw(s));
    }
}

// ── Input encoding ──
//
// A logical input is (kind, ch, key, mods). `ch` is a Unicode scalar for
// TypeChar/Shortcut. `key`/`mods` describe the combo for RawKey:
// key = 0x1000_0000 | codepoint for Key::Char, 0x2000_0000 | n for F-keys,
// or a named-key value below; mods bit 1 = ctrl, 2 = alt, 4 = shift.

const KEY_CHAR_BASE: u32 = 0x1000_0000;
const KEY_F_BASE: u32 = 0x2000_0000;

fn encode_key(key: Key) -> u32 {
    match key {
        Key::Char(c) => KEY_CHAR_BASE | c as u32,
        Key::F(n) => KEY_F_BASE | n as u32,
        Key::Enter => 1,
        Key::Escape => 2,
        Key::Tab => 3,
        Key::Space => 4,
        Key::Up => 5,
        Key::Down => 6,
        Key::Left => 7,
        Key::Right => 8,
        Key::Home => 9,
        Key::End => 10,
        Key::Delete => 11,
        Key::Backspace => 12,
        Key::PageUp => 13,
        Key::PageDown => 14,
        Key::MediaPlayPause => 15,
        Key::MediaNextTrack => 16,
        Key::MediaPreviousTrack => 17,
        Key::MediaStop => 18,
    }
}

fn decode_key(raw: u32) -> Option<Key> {
    if raw & KEY_CHAR_BASE != 0 {
        return char::from_u32(raw & 0x00FF_FFFF).map(Key::Char);
    }
    if raw & KEY_F_BASE != 0 {
        return Some(Key::F((raw & 0xFF) as u8));
    }
    Some(match raw {
        1 => Key::Enter,
        2 => Key::Escape,
        3 => Key::Tab,
        4 => Key::Space,
        5 => Key::Up,
        6 => Key::Down,
        7 => Key::Left,
        8 => Key::Right,
        9 => Key::Home,
        10 => Key::End,
        11 => Key::Delete,
        12 => Key::Backspace,
        13 => Key::PageUp,
        14 => Key::PageDown,
        15 => Key::MediaPlayPause,
        16 => Key::MediaNextTrack,
        17 => Key::MediaPreviousTrack,
        18 => Key::MediaStop,
        _ => return None,
    })
}

fn encode_input(input: &LogicalInput) -> (u32, u32, u32, u32) {
    use LogicalInput as L;
    let simple = |kind: u32| (kind, 0, 0, 0);
    match input {
        L::NavigateNext => simple(0),
        L::NavigatePrev => simple(1),
        L::TreeUp => simple(2),
        L::TreeDown => simple(3),
        L::TreeLeft => simple(4),
        L::TreeRight => simple(5),
        L::Shortcut(c) => (6, *c as u32, 0, 0),
        L::Activate => simple(7),
        L::SecondaryActivate => simple(8),
        L::MoveUp => simple(9),
        L::MoveDown => simple(10),
        L::MoveLeft => simple(11),
        L::MoveRight => simple(12),
        L::MoveWordLeft => simple(13),
        L::MoveWordRight => simple(14),
        L::MoveToLineStart => simple(15),
        L::MoveToLineEnd => simple(16),
        L::MoveToDocStart => simple(17),
        L::MoveToDocEnd => simple(18),
        L::MoveLineUp => simple(19),
        L::MoveLineDown => simple(20),
        L::SelectLeft => simple(21),
        L::SelectRight => simple(22),
        L::SelectWordLeft => simple(23),
        L::SelectWordRight => simple(24),
        L::SelectToLineStart => simple(25),
        L::SelectToLineEnd => simple(26),
        L::SelectToDocStart => simple(27),
        L::SelectToDocEnd => simple(28),
        L::SelectLineUp => simple(29),
        L::SelectLineDown => simple(30),
        L::SelectAll => simple(31),
        L::TypeChar(c) => (32, *c as u32, 0, 0),
        L::DeleteBackward => simple(33),
        L::DeleteForward => simple(34),
        L::DeleteWordBackward => simple(35),
        L::DeleteWordForward => simple(36),
        L::Copy => simple(37),
        L::Cut => simple(38),
        L::Paste => simple(39),
        L::SpeakFocus => simple(40),
        L::Dismiss => simple(41),
        L::RawKey(combo) => {
            let mods = (combo.ctrl as u32) | ((combo.alt as u32) << 1) | ((combo.shift as u32) << 2);
            (42, 0, encode_key(combo.key), mods)
        }
    }
}

fn decode_input(kind: u32, ch: u32, key: u32, mods: u32) -> Option<LogicalInput> {
    use LogicalInput as L;
    Some(match kind {
        0 => L::NavigateNext,
        1 => L::NavigatePrev,
        2 => L::TreeUp,
        3 => L::TreeDown,
        4 => L::TreeLeft,
        5 => L::TreeRight,
        6 => L::Shortcut(char::from_u32(ch)?),
        7 => L::Activate,
        8 => L::SecondaryActivate,
        9 => L::MoveUp,
        10 => L::MoveDown,
        11 => L::MoveLeft,
        12 => L::MoveRight,
        13 => L::MoveWordLeft,
        14 => L::MoveWordRight,
        15 => L::MoveToLineStart,
        16 => L::MoveToLineEnd,
        17 => L::MoveToDocStart,
        18 => L::MoveToDocEnd,
        19 => L::MoveLineUp,
        20 => L::MoveLineDown,
        21 => L::SelectLeft,
        22 => L::SelectRight,
        23 => L::SelectWordLeft,
        24 => L::SelectWordRight,
        25 => L::SelectToLineStart,
        26 => L::SelectToLineEnd,
        27 => L::SelectToDocStart,
        28 => L::SelectToDocEnd,
        29 => L::SelectLineUp,
        30 => L::SelectLineDown,
        31 => L::SelectAll,
        32 => L::TypeChar(char::from_u32(ch)?),
        33 => L::DeleteBackward,
        34 => L::DeleteForward,
        35 => L::DeleteWordBackward,
        36 => L::DeleteWordForward,
        37 => L::Copy,
        38 => L::Cut,
        39 => L::Paste,
        40 => L::SpeakFocus,
        41 => L::Dismiss,
        42 => L::RawKey(KeyCombo::new(
            decode_key(key)?,
            mods & 1 != 0,
            mods & 2 != 0,
            mods & 4 != 0,
        )),
        _ => return None,
    })
}

// ── Ui ──

#[no_mangle]
pub extern "C" fn srui_ui_new() -> *mut Ui {
    Box::into_raw(Box::new(Ui::new()))
}

/// # Safety
/// `ui` must be a pointer from `srui_ui_new`, not used afterwards.
#[no_mangle]
pub unsafe extern "C" fn srui_ui_free(ui: *mut Ui) {
    if !ui.is_null() {
        drop(Box::from_raw(ui));
    }
}

#[no_mangle]
pub unsafe extern "C" fn srui_ui_set_now(ui: *mut Ui, now_ms: u64) {
    (*ui).set_now(now_ms);
}

#[no_mangle]
pub unsafe extern "C" fn srui_ui_ensure_focus(ui: *mut Ui) -> bool {
    (*ui).ensure_focus()
}

#[no_mangle]
pub unsafe extern "C" fn srui_ui_set_focus(ui: *mut Ui, node: u64) {
    if let Some(id) = node_in(node) {
        (*ui).set_focus(id);
    }
}

#[no_mangle]
pub unsafe extern "C" fn srui_ui_focus(ui: *mut Ui) -> u64 {
    (*ui).focus().map(|n| n.to_ffi()).unwrap_or(0)
}

#[no_mangle]
pub unsafe extern "C" fn srui_ui_handle_input(
    ui: *mut Ui,
    kind: u32,
    ch: u32,
    key: u32,
    mods: u32,
) -> bool {
    match decode_input(kind, ch, key, mods) {
        Some(input) => (*ui).handle_input(&input),
        None => false,
    }
}

#[no_mangle]
pub unsafe extern "C" fn srui_ui_announce(ui: *mut Ui, text: *const c_char) {
    (*ui).announce(str_in(text));
}

/// Re-announce the focused node with its context labels (preceding
/// Label siblings) — the dialog-open announcement.
#[no_mangle]
pub unsafe extern "C" fn srui_ui_reannounce_with_context(ui: *mut Ui) {
    (*ui).reannounce_with_context();
}

/// Register a periodic ticker; Tick events (kind 200, num0 = id) fire at
/// `set_now` resolution. Returns the ticker id.
#[no_mangle]
pub unsafe extern "C" fn srui_ui_add_ticker(ui: *mut Ui, interval_ms: u64) -> u64 {
    (*ui).add_ticker(interval_ms)
}

#[no_mangle]
pub unsafe extern "C" fn srui_ui_remove_ticker(ui: *mut Ui, id: u64) {
    (*ui).remove_ticker(id);
}

#[no_mangle]
pub unsafe extern "C" fn srui_ui_set_primary(ui: *mut Ui, node: u64) {
    if let Some(id) = node_in(node) {
        (*ui).set_primary(id);
    }
}

#[no_mangle]
pub unsafe extern "C" fn srui_ui_set_cancel(ui: *mut Ui, node: u64) {
    if let Some(id) = node_in(node) {
        (*ui).set_cancel(id);
    }
}

#[no_mangle]
pub unsafe extern "C" fn srui_ui_push_layer(ui: *mut Ui) {
    (*ui).push_layer();
}

#[no_mangle]
pub unsafe extern "C" fn srui_ui_pop_layer(ui: *mut Ui) {
    (*ui).pop_layer();
}

#[no_mangle]
pub unsafe extern "C" fn srui_ui_remove(ui: *mut Ui, node: u64) {
    if let Some(id) = node_in(node) {
        (*ui).remove(id);
    }
}

#[no_mangle]
pub unsafe extern "C" fn srui_ui_set_hidden(ui: *mut Ui, node: u64, hidden: bool) {
    if let Some(id) = node_in(node) {
        (*ui).set_hidden(id, hidden);
    }
}

#[no_mangle]
pub unsafe extern "C" fn srui_ui_set_disabled(ui: *mut Ui, node: u64, disabled: bool) {
    if let Some(id) = node_in(node) {
        (*ui).set_disabled(id, disabled);
    }
}

#[no_mangle]
pub unsafe extern "C" fn srui_ui_set_node_name(ui: *mut Ui, node: u64, name: *const c_char) {
    if let Some(id) = node_in(node) {
        (*ui).set_node_name(id, str_in(name));
    }
}

#[no_mangle]
pub unsafe extern "C" fn srui_ui_set_node_description(
    ui: *mut Ui,
    node: u64,
    description: *const c_char,
) {
    if let Some(id) = node_in(node) {
        (*ui).set_node_description(id, str_in(description));
    }
}

/// Attach a shortcut to a widget. `combo` is the config form
/// ("ctrl+shift+s"); `action` is 0 = jump, 1 = activate, 2 = both.
/// Returns false when the combo fails to parse or the action is unknown.
#[no_mangle]
pub unsafe extern "C" fn srui_ui_add_shortcut(
    ui: *mut Ui,
    node: u64,
    combo: *const c_char,
    action: u32,
) -> bool {
    let Some(id) = node_in(node) else {
        return false;
    };
    let Ok(combo) = KeyCombo::parse_config(str_in(combo)) else {
        return false;
    };
    let action = match action {
        0 => ShortcutAction::Jump,
        1 => ShortcutAction::Activate,
        2 => ShortcutAction::JumpAndActivate,
        _ => return false,
    };
    (*ui).add_shortcut(id, combo, action);
    true
}

/// Remove every shortcut from a widget.
#[no_mangle]
pub unsafe extern "C" fn srui_ui_clear_shortcuts(ui: *mut Ui, node: u64) {
    if let Some(id) = node_in(node) {
        (*ui).clear_shortcuts(id);
    }
}

// ── Node constructors ──

#[no_mangle]
pub unsafe extern "C" fn srui_ui_text_label(
    ui: *mut Ui,
    parent: u64,
    text: *const c_char,
) -> u64 {
    (*ui).text_label(node_in(parent), str_in(text)).to_ffi()
}

#[no_mangle]
pub unsafe extern "C" fn srui_ui_group(ui: *mut Ui, parent: u64, name: *const c_char) -> u64 {
    (*ui).group(node_in(parent), str_in(name)).to_ffi()
}

/// A custom widget: focusable, no spoken role, no built-in behavior —
/// every key falls through the core to the host's bindings.
#[no_mangle]
pub unsafe extern "C" fn srui_ui_custom(ui: *mut Ui, parent: u64, name: *const c_char) -> u64 {
    (*ui).custom(node_in(parent), str_in(name)).to_ffi()
}

#[no_mangle]
pub unsafe extern "C" fn srui_ui_button(ui: *mut Ui, parent: u64, name: *const c_char) -> u64 {
    (*ui).button(node_in(parent), str_in(name)).to_ffi()
}

#[no_mangle]
pub unsafe extern "C" fn srui_ui_checkbox(
    ui: *mut Ui,
    parent: u64,
    name: *const c_char,
    checked: bool,
) -> u64 {
    (*ui).checkbox(node_in(parent), str_in(name), checked).to_ffi()
}

#[no_mangle]
pub unsafe extern "C" fn srui_ui_editbox(
    ui: *mut Ui,
    parent: u64,
    name: *const c_char,
    text: *const c_char,
    multiline: bool,
) -> u64 {
    let (parent, name, text) = (node_in(parent), str_in(name), str_in(text));
    if multiline {
        (*ui).editbox_multiline(parent, name, text).to_ffi()
    } else {
        (*ui).editbox(parent, name, text).to_ffi()
    }
}

#[no_mangle]
pub unsafe extern "C" fn srui_ui_listbox(
    ui: *mut Ui,
    parent: u64,
    name: *const c_char,
    items: *const *const c_char,
    items_len: usize,
    numbered: bool,
) -> u64 {
    (*ui)
        .listbox(
            node_in(parent),
            str_in(name),
            strings_in(items, items_len),
            numbered,
        )
        .to_ffi()
}

#[no_mangle]
pub unsafe extern "C" fn srui_ui_filter_listbox(
    ui: *mut Ui,
    parent: u64,
    name: *const c_char,
    items: *const *const c_char,
    items_len: usize,
) -> u64 {
    (*ui)
        .filter_listbox(node_in(parent), str_in(name), strings_in(items, items_len))
        .to_ffi()
}

#[no_mangle]
#[allow(clippy::too_many_arguments)]
pub unsafe extern "C" fn srui_ui_slider(
    ui: *mut Ui,
    parent: u64,
    name: *const c_char,
    value: i32,
    min: i32,
    max: i32,
    small_step: i32,
    large_step: i32,
    unit: *const c_char,
) -> u64 {
    let slider = Slider::new(value, min, max)
        .with_steps(small_step, large_step)
        .with_unit(str_in(unit));
    (*ui).slider_widget(node_in(parent), str_in(name), slider).to_ffi()
}

#[no_mangle]
pub unsafe extern "C" fn srui_ui_tab_control(
    ui: *mut Ui,
    parent: u64,
    name: *const c_char,
    tabs: *const *const c_char,
    tabs_len: usize,
    active: usize,
) -> u64 {
    (*ui)
        .tab_control(node_in(parent), str_in(name), strings_in(tabs, tabs_len), active)
        .to_ffi()
}

#[no_mangle]
pub unsafe extern "C" fn srui_ui_shortcut_field(
    ui: *mut Ui,
    parent: u64,
    name: *const c_char,
) -> u64 {
    (*ui).shortcut_field(node_in(parent), str_in(name)).to_ffi()
}

// ── Widget state access ──

#[no_mangle]
pub unsafe extern "C" fn srui_ui_checkbox_checked(ui: *mut Ui, node: u64) -> bool {
    node_in(node)
        .and_then(|id| (*ui).widget::<CheckBox>(id))
        .map(|c| c.checked)
        .unwrap_or(false)
}

/// Returns a string to free with `srui_string_free`, or null.
#[no_mangle]
pub unsafe extern "C" fn srui_ui_editbox_text(ui: *mut Ui, node: u64) -> *mut c_char {
    match node_in(node).and_then(|id| (*ui).widget::<EditBox>(id)) {
        Some(edit) => str_out(edit.text()),
        None => std::ptr::null_mut(),
    }
}

#[no_mangle]
pub unsafe extern "C" fn srui_ui_set_editbox_text(ui: *mut Ui, node: u64, text: *const c_char) {
    if let Some(id) = node_in(node) {
        (*ui).set_editbox_text(id, str_in(text));
    }
}

#[no_mangle]
pub unsafe extern "C" fn srui_ui_set_editbox_read_only(ui: *mut Ui, node: u64, read_only: bool) {
    if let Some(id) = node_in(node) {
        (*ui).set_editbox_read_only(id, read_only);
    }
}

/// -1 when the node is not a listbox.
#[no_mangle]
pub unsafe extern "C" fn srui_ui_listbox_selected(ui: *mut Ui, node: u64) -> i64 {
    node_in(node)
        .and_then(|id| (*ui).widget::<ListBox>(id))
        .map(|l| l.selected() as i64)
        .unwrap_or(-1)
}

/// Returns a string to free with `srui_string_free`, or null.
#[no_mangle]
pub unsafe extern "C" fn srui_ui_listbox_selected_item(ui: *mut Ui, node: u64) -> *mut c_char {
    match node_in(node)
        .and_then(|id| (*ui).widget::<ListBox>(id))
        .and_then(|l| l.selected_item())
    {
        Some(item) => str_out(item.to_string()),
        None => std::ptr::null_mut(),
    }
}

#[no_mangle]
pub unsafe extern "C" fn srui_ui_set_list_items(
    ui: *mut Ui,
    node: u64,
    items: *const *const c_char,
    items_len: usize,
) {
    if let Some(id) = node_in(node) {
        (*ui).set_list_items(id, strings_in(items, items_len));
    }
}

/// Returns a string to free with `srui_string_free`, or null.
#[no_mangle]
pub unsafe extern "C" fn srui_ui_filter_selected_item(ui: *mut Ui, node: u64) -> *mut c_char {
    match node_in(node)
        .and_then(|id| (*ui).widget::<FilterListBox>(id))
        .and_then(|f| f.selected_item())
    {
        Some(item) => str_out(item),
        None => std::ptr::null_mut(),
    }
}

#[no_mangle]
pub unsafe extern "C" fn srui_ui_slider_value(ui: *mut Ui, node: u64) -> i32 {
    node_in(node)
        .and_then(|id| (*ui).widget::<Slider>(id))
        .map(|s| s.value())
        .unwrap_or(0)
}

#[no_mangle]
pub unsafe extern "C" fn srui_ui_set_slider_value(ui: *mut Ui, node: u64, value: i32) {
    if let Some(id) = node_in(node) {
        (*ui).set_slider_value(id, value);
    }
}

/// -1 when the node is not a tab control.
#[no_mangle]
pub unsafe extern "C" fn srui_ui_tab_active(ui: *mut Ui, node: u64) -> i64 {
    node_in(node)
        .and_then(|id| (*ui).widget::<TabControl>(id))
        .map(|t| t.active() as i64)
        .unwrap_or(-1)
}

/// The captured combo in config form ("ctrl+shift+s"), to free with
/// `srui_string_free`; null when empty or not a shortcut field.
#[no_mangle]
pub unsafe extern "C" fn srui_ui_shortcut_combo(ui: *mut Ui, node: u64) -> *mut c_char {
    match node_in(node)
        .and_then(|id| (*ui).widget::<ShortcutField>(id))
        .and_then(|f| f.combo())
    {
        Some(combo) => str_out(combo.to_string_config()),
        None => std::ptr::null_mut(),
    }
}

// ── Event drain ──

/// One drained output event, flattened.
///
/// kind: 1 = accessibility (num0 = variant: 0 focused, 1 typing,
/// 2 text-nav, 3 selection, 4 item-nav, 5 tab-change, 6 slider-change,
/// 7 filter, 8 clipboard, 9 announce; `speech` = rendered utterance,
/// possibly null when the event renders to silence);
/// 100 = activated, 101 = secondary-activated, 102 = toggled
/// (num0 = checked), 103 = changed.
#[repr(C)]
pub struct SruiEvent {
    pub kind: u32,
    pub node: u64,
    pub num0: i64,
    pub speech: *mut c_char,
}

fn flatten_event(event: OutputEvent) -> SruiEvent {
    match event {
        OutputEvent::Accessibility(a) => {
            use AccessibilityEvent as A;
            let (variant, node) = match &a {
                A::Focused { node, .. } => (0, Some(*node)),
                A::Typing { node, .. } => (1, Some(*node)),
                A::TextNav { node, .. } => (2, Some(*node)),
                A::Selection { node, .. } => (3, Some(*node)),
                A::ItemNav { node, .. } => (4, Some(*node)),
                A::TabChange { node, .. } => (5, Some(*node)),
                A::SliderChange { node, .. } => (6, Some(*node)),
                A::Filter { node, .. } => (7, Some(*node)),
                A::Clipboard { node, .. } => (8, Some(*node)),
                A::Announce { .. } => (9, None),
            };
            let speech = match render_event(&a) {
                Some(text) => str_out(text),
                None => std::ptr::null_mut(),
            };
            SruiEvent {
                kind: 1,
                node: node.map(|n| n.to_ffi()).unwrap_or(0),
                num0: variant,
                speech,
            }
        }
        OutputEvent::Widget(w) => {
            let (kind, node, num0) = match w {
                WidgetEvent::Activated { node } => (100, node, 0),
                WidgetEvent::SecondaryActivated { node } => (101, node, 0),
                WidgetEvent::Toggled { node, checked } => (102, node, checked as i64),
                WidgetEvent::Changed { node } => (103, node, 0),
            };
            SruiEvent {
                kind,
                node: node.to_ffi(),
                num0,
                speech: std::ptr::null_mut(),
            }
        }
        OutputEvent::Tick { ticker } => SruiEvent {
            kind: 200,
            node: 0,
            num0: ticker as i64,
            speech: std::ptr::null_mut(),
        },
    }
}

/// Drain the coalesced output queue. `*out_events` receives an array of
/// `*out_len` events to release with `srui_events_free`.
#[no_mangle]
pub unsafe extern "C" fn srui_ui_drain(
    ui: *mut Ui,
    out_events: *mut *mut SruiEvent,
    out_len: *mut usize,
) {
    let flattened: Vec<SruiEvent> = (*ui).drain_events().into_iter().map(flatten_event).collect();
    let boxed = flattened.into_boxed_slice();
    *out_len = boxed.len();
    *out_events = Box::into_raw(boxed) as *mut SruiEvent;
}

/// # Safety
/// `events`/`len` must come from `srui_ui_drain`, freed exactly once.
#[no_mangle]
pub unsafe extern "C" fn srui_events_free(events: *mut SruiEvent, len: usize) {
    if events.is_null() {
        return;
    }
    let slice = Box::from_raw(std::ptr::slice_from_raw_parts_mut(events, len));
    for event in slice.iter() {
        srui_string_free(event.speech);
    }
}

// ── SDL host ──

/// One pumped host event. kind: 0 = quit, 1 = key-down, 2 = alt-tap,
/// 3 = input (input_kind/ch/key/mods per the input encoding), 4 = a
/// physical key transition for game-style input (key/mods = the combo;
/// input_kind = flags: bit 0 pressed, bit 1 repeat), 5 = the window lost
/// keyboard focus (hosts zero held-key state — releases won't arrive).
#[repr(C)]
pub struct SruiHostEvent {
    pub kind: u32,
    pub input_kind: u32,
    pub ch: u32,
    pub key: u32,
    pub mods: u32,
}

/// Null on failure (SDL init/window creation).
#[no_mangle]
pub unsafe extern "C" fn srui_host_new(title: *const c_char, width: u32, height: u32) -> *mut SdlHost {
    match SdlHost::new(str_in(title), width, height) {
        Ok(host) => Box::into_raw(Box::new(host)),
        Err(_) => std::ptr::null_mut(),
    }
}

/// # Safety
/// `host` must be a pointer from `srui_host_new`, not used afterwards.
#[no_mangle]
pub unsafe extern "C" fn srui_host_free(host: *mut SdlHost) {
    if !host.is_null() {
        drop(Box::from_raw(host));
    }
}

/// Install the host's system clipboard on a Ui.
#[no_mangle]
pub unsafe extern "C" fn srui_ui_use_host_clipboard(ui: *mut Ui, host: *mut SdlHost) {
    (*ui).set_clipboard((*host).clipboard());
}

/// Pump SDL for up to `timeout_ms`; `*out_events` receives an array of
/// `*out_len` host events to release with `srui_host_events_free`.
#[no_mangle]
pub unsafe extern "C" fn srui_host_pump(
    host: *mut SdlHost,
    timeout_ms: u32,
    out_events: *mut *mut SruiHostEvent,
    out_len: *mut usize,
) {
    let flattened: Vec<SruiHostEvent> = (*host)
        .pump(timeout_ms)
        .into_iter()
        .map(|event| match event {
            HostEvent::Quit => SruiHostEvent { kind: 0, input_kind: 0, ch: 0, key: 0, mods: 0 },
            HostEvent::KeyDown => SruiHostEvent { kind: 1, input_kind: 0, ch: 0, key: 0, mods: 0 },
            HostEvent::AltTap => SruiHostEvent { kind: 2, input_kind: 0, ch: 0, key: 0, mods: 0 },
            HostEvent::Input(input) => {
                let (input_kind, ch, key, mods) = encode_input(&input);
                SruiHostEvent { kind: 3, input_kind, ch, key, mods }
            }
            HostEvent::Key { combo, pressed, repeat } => SruiHostEvent {
                kind: 4,
                input_kind: (pressed as u32) | ((repeat as u32) << 1),
                ch: 0,
                key: encode_key(combo.key),
                mods: (combo.ctrl as u32) | ((combo.alt as u32) << 1) | ((combo.shift as u32) << 2),
            },
            HostEvent::FocusLost => SruiHostEvent { kind: 5, input_kind: 0, ch: 0, key: 0, mods: 0 },
        })
        .collect();
    let boxed = flattened.into_boxed_slice();
    *out_len = boxed.len();
    *out_events = Box::into_raw(boxed) as *mut SruiHostEvent;
}

/// # Safety
/// `events`/`len` must come from `srui_host_pump`, freed exactly once.
#[no_mangle]
pub unsafe extern "C" fn srui_host_events_free(events: *mut SruiHostEvent, len: usize) {
    if !events.is_null() {
        drop(Box::from_raw(std::ptr::slice_from_raw_parts_mut(events, len)));
    }
}

/// Parse a config-form combo ("ctrl+shift+s") into the flat key/mods
/// encoding used by inputs and host key events. Returns false (outputs
/// untouched) on parse failure.
#[no_mangle]
pub unsafe extern "C" fn srui_combo_parse(
    combo: *const c_char,
    out_key: *mut u32,
    out_mods: *mut u32,
) -> bool {
    match KeyCombo::parse_config(str_in(combo)) {
        Ok(parsed) => {
            *out_key = encode_key(parsed.key);
            *out_mods = (parsed.ctrl as u32)
                | ((parsed.alt as u32) << 1)
                | ((parsed.shift as u32) << 2);
            true
        }
        Err(_) => false,
    }
}

// ── Speech ──

/// Null when no backend is available.
#[no_mangle]
pub extern "C" fn srui_speech_new() -> *mut Speech {
    match Speech::new() {
        Ok(speech) => Box::into_raw(Box::new(speech)),
        Err(_) => std::ptr::null_mut(),
    }
}

/// # Safety
/// `speech` must be a pointer from `srui_speech_new`, not used afterwards.
#[no_mangle]
pub unsafe extern "C" fn srui_speech_free(speech: *mut Speech) {
    if !speech.is_null() {
        drop(Box::from_raw(speech));
    }
}

#[no_mangle]
pub unsafe extern "C" fn srui_speech_speak(
    speech: *mut Speech,
    text: *const c_char,
    interrupt: bool,
) -> bool {
    (*speech).speak(str_in(text), interrupt).is_ok()
}

#[no_mangle]
pub unsafe extern "C" fn srui_speech_stop(speech: *mut Speech) {
    (*speech).stop();
}

/// Returns a string to free with `srui_string_free`.
#[no_mangle]
pub unsafe extern "C" fn srui_speech_backend_name(speech: *mut Speech) -> *mut c_char {
    str_out((*speech).backend_name())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn input_roundtrip() {
        let inputs = [
            LogicalInput::NavigateNext,
            LogicalInput::Shortcut('w'),
            LogicalInput::TypeChar('é'),
            LogicalInput::SelectAll,
            LogicalInput::Dismiss,
            LogicalInput::RawKey(KeyCombo::ctrl(Key::Char('g'))),
            LogicalInput::RawKey(KeyCombo::ctrl_shift(Key::Tab)),
            LogicalInput::RawKey(KeyCombo::plain(Key::F(5))),
            LogicalInput::RawKey(KeyCombo::alt(Key::PageDown)),
        ];
        for input in inputs {
            let (kind, ch, key, mods) = encode_input(&input);
            assert_eq!(decode_input(kind, ch, key, mods).as_ref(), Some(&input));
        }
    }
}
