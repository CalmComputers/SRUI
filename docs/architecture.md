# SRUI Architecture

# 1. Overview

SRUI is a screen-reader-first UI toolkit. It maintains a retained semantic tree of widgets, processes keyboard input against that tree, and emits structured output events describing what the user should perceive. It produces no pixels and never will: visual rendering is permanently out of scope. What a host application does with the output events — synthesize speech, drive a braille display, back a platform accessibility API — is the host's decision, made by attaching readers (section 7).

The toolkit is usable from Rust natively and from C# through a C ABI. The core is written in Rust; bindings never reimplement behavior.

# 2. Design Principles

## 2.1. Sans-IO core

The core owns no platform resources: no TTS engine, no clipboard, no window, no event loop, no file access, no wall clock (time is injected). The host pushes input in and drains events out. Platform services the core needs (clipboard) are injected as trait objects. This keeps the entire engine headless and deterministic under test.

## 2.2. The tree is the source of truth for widget state

Widget-local state — an edit box's text, a list's selection index, a checkbox's value — lives in the node, not in the application model. Programs mutate nodes through handles and learn about user-driven changes through output events. Application state synchronization is the program's responsibility (a future reactive layer will automate it).

## 2.3. Structured events out; readers render

The core never speaks. It emits structured accessibility events carrying complete semantic payloads. Speech composition is provided as a pure library (event in, utterance string out, verbosity-parameterized) that readers may use or bypass. This is what lets one event stream feed a self-voicing reader, a braille reader, a UIA provider, and a test harness simultaneously.

## 2.4. Policy-free

Application vocabulary stays out of the core: no application command names, no command categories, no persistence formats, no application-global shortcuts. The core supplies mechanisms (key combos, binding maps, conflict detection) and the host supplies policy. Serialization of user configuration is host-owned; the core exposes stable string forms (for example `"ctrl+shift+s"` for key combos) and plain data structures.

# 3. Layering

| Layer | Language | Role |
|---|---|---|
| `srui-core` | Rust | Retained tree, widget behavior, focus/navigation, input vocabulary, output events, speech rendering library. Pure; no platform dependencies. |
| `srui-sdl` | Rust | SDL3 host layer: a focus-receiving window, the event pump, physical→logical input translation, and the physical key stream (section 6.4). Optional — any host that can produce `LogicalInput` works. |
| `srui-prism` / `srui-prism-sys` | Rust over C | Speech and braille output through Prism (vendored; builds via CMake), which routes to the running screen reader or platform TTS. The reference speech reader's output channel. |
| `srui-ffi` | Rust (cdylib) | C ABI over the core, the SDL host, and Prism speech — one native surface for bindings: opaque handles, flat input/event structs, event drain. |
| `Srui.Net` | C# | Idiomatic .NET wrapper over the C ABI: classes, records, hand-written P/Invoke. `dotnet/SruiDemo` mirrors the Rust demo. |
| `Srui.Audio` / `srui-audio-native` | C# over C | Game audio for C# consumers: 3D positioning (Horizon-style pan/volume or Steam Audio HRTF), sound groups with a native effect chain (convolution reverb, EQ, filter, distortion, vocoder, disperser, delay), pitch tweens, offline time-stretch. All DSP stays native (cosmos.dll: miniaudio + vendored cosmos nodes, decoding wav, flac, mp3, vorbis, and opus); C# orchestrates with zero steady-state allocation. Everything is edge-triggered except time-based automation, which `SoundManager.Tick` applies: `SruiApp` owns an on-demand manager (`SruiApp.Audio`) and ticks it from the event loop at the loop cadence (about 5 ms at idle); loop-less consumers tick manually. Rust games use cosmos-audio directly instead. |
| Readers | host-side | Consumers of the output event stream: self-voicing speech, braille, UIA provider, logging/test readers. |

Rust programs consume `srui-core` directly; there is no FFI on the Rust path. A reactive layer over the retained API is anticipated but deferred. `srui-demo` wires the whole stack together end to end: SDL events in, Prism speech out.

# 4. The Semantic Tree

## 4.1. Nodes and handles

The tree owns all nodes in a slotmap; a `NodeId` is a generational handle that is O(1) to resolve, stable for the node's lifetime, and never dangles silently (a stale handle resolves to nothing rather than to a reused slot). `NodeId` converts losslessly to and from `u64`, which is its FFI representation. Each node stores its parent, an ordered child list, its semantic label, and its widget behavior/state.

## 4.2. The golden six

Every node carries the six semantic properties a screen reader announces, in NVDA order: name, role, value, states, description, shortcut. `Role` identifies the control type (button, check box, edit box, list, group, label, tab control, slider, and so on) and knows which keys that control type consumes during normal interaction — the basis of shortcut conflict detection. The `Custom` role is deliberately empty: focusable, no spoken role word, no reserved keys, no built-in behavior — a widget whose entire interaction is host-defined (section 6.4), the usual base for game surfaces. `States` is a bitflag set of conditions the user cannot directly change (focused, disabled, required, warning, hidden). The shortcut slot holds the widget's shortcut list (section 8); announcements speak the first shortcut added, in its spoken display form. A node's name is optional; a nameless node announces as role and value only, for widgets whose identity is carried by their container.

## 4.3. Mutation

Programs build and change UI by mutating the tree: insert a node under a parent at an index, remove a subtree, reparent, and set label fields or widget state through the handle. Mutations are synchronous and immediately visible. Mutations that affect what the user is perceiving (the focused node's value changes, the focused subtree is removed) cause the core to emit appropriate accessibility events; mutations elsewhere in the tree are silent. A programmatic change to a focused widget's single value state — a slider's value, a list's selection, the active tab — speaks exactly what the equivalent user-driven change speaks (the new value alone, so a ticking progress slider stays terse); other label changes re-announce the widget in full.

## 4.4. Layers

The tree maintains a stack of layers for modal UI. Each layer has its own roots, focus, and default primary/cancel widgets (Enter and Escape targets). Only the top layer is navigable. Pushing a layer opens a dialog or palette; popping it removes the layer's nodes and restores the previous layer's focus. The base layer cannot be popped.

Dialog conventions live in the bindings, not the core: there is no dialog role (the prompt is a Label preceding the widgets, announced as a context label via the focused-with-context re-announcement), Escape closes a dialog automatically when no cancel widget claims it, and the canned dialogs (message, confirm, custom button rows, reviewable read-only status text) are language-level helpers — `SruiDialogs` in C# — because they are too high-level to share across languages profitably. Canned dialog buttons carry the Windows-conventional Alt+letter activation shortcut, assigned first-free-letter within the dialog and spoken with the button; widget shortcuts only match within the active layer, so these never collide with the window underneath.

# 5. Widget Behavior

Widget behavior is trait-dispatched: each node holds an implementation of a `Widget` trait that handles logical input directed at the node when focused, mutates its own state, and emits output events. Built-in roles ship with the core.

Custom widgets — new `Widget` implementations — are written in Rust only. The trait is not implementable across the FFI boundary; the cost (function-pointer vtables, reentrancy across languages) outweighs the benefit. Other languages extend the toolkit by composing primitives and by subclassing their binding-side wrapper classes: a C# subclass of a wrapper adds application semantics, event handling, and composition around a node whose core behavior remains in Rust. A `Role::Custom` node is the degenerate case: it carries no `Widget` implementation and needs none — its interaction lives entirely in host bindings over the physical key stream (section 6.4).

# 6. Input

## 6.1. Vocabulary

A `KeyCombo` is a physical key plus ctrl/alt/shift modifier state, with a spoken display form ("control shift s") and a compact config form ("ctrl+shift+s") that round-trips through parsing. A `LogicalInput` is the semantic layer above it: navigation (next/previous, hierarchy moves, mnemonic), widget verbs (activate, move by character/word/line, select, type, delete, clipboard), `SpeakFocus`, `Dismiss`, and `RawKey` for combos with no semantic mapping. The default physical-to-logical map is host-replaceable; the host is responsible for translating platform key events into `KeyCombo`s.

## 6.2. Dispatch

Input events are pushed to the core one at a time from a host-side queue; there is no frame granularity. Each event is dispatched in a fixed claim order: the focused node's widget gets first claim (guided by its role's reserved-key table), then framework navigation and layer defaults (tab ring, hierarchy navigation, primary/cancel), then widget shortcuts (section 8), then the host's key bindings. Whatever handles the event emits the corresponding output events; unclaimed input falls through to the host.

## 6.3. Time

The core has no clock; the host feeds one through `set_now` (monotonic milliseconds), ideally once per loop iteration. Typeahead timeouts and tickers are both observed there. A ticker (`add_ticker`) emits a `Tick` output event each time its interval elapses, at `set_now` resolution, drift-tolerant: a late check fires once rather than bursting to catch up.

## 6.4. The physical key stream

Alongside the logical stream, the host layer reports raw key transitions for game-style input: the initial press, auto-repeats, and the release of every key with a `KeyCombo` form (bare modifier taps have none). This stream never enters the core — the core is semantic, and key bindings are host policy (section 9) — and it is independent of the claim order: a press is reported even when the corresponding logical input is consumed. Because a release can never be consumed and a press can never be hidden, the pairing a game relies on is unconditional. The host also signals the window losing keyboard focus, which orphans held keys: game state that tracks them must be zeroed, as the releases will never arrive.

The C# binding builds per-widget key bindings on this stream: handlers attach to any widget for a combo and phase (press, repeat, release) and fire while that widget is focused, with unclaimed transitions falling to an app-level hook. Press and repeat match the exact combo; release matches the key alone, so a modifier pressed mid-hold cannot orphan the release.

# 7. Output Events and Readers

## 7.1. Event streams

The core produces a single ordered stream of output events in three families. Accessibility events describe perception: focus moved (with the full label and any context labels), text typed or deleted, cursor moved, selection changed, list item changed (with position), tab changed, slider changed, filter results changed, boundary hit, plus a free-form announce escape hatch. Editor events deliberately carry only perceptual content (the grapheme, the echoed word, the spoken context) — never document text; a reader that needs the line at the cursor queries the editor through the node. A read-only editor swallows typing silently — nothing was inserted, so there is nothing to echo — and lets Enter fall through to the layer's primary widget. Widget events describe intent for the program: activated, toggled, value changed, selection changed. Tick events report elapsed ticker intervals. Programs react to widget and tick events; readers react to accessibility events.

## 7.2. Draining and coalescing

The host drains the event queue when it chooses (typically after each input event or mutation batch). Coalescing is applied at drain time: for state-describing events (focus, selection, item/tab/slider/filter changes), only the latest instance of each event kind survives a drain — an intermediate focus move is discarded in favor of the settled one; transient events (typing echoes, announcements) and all widget events survive in order. Interruption policy — silencing speech on a keypress, utterance length limits — belongs to the speech reader, not the core.

## 7.3. Readers

A reader is any consumer of accessibility events. The reference reader is a self-voicing speech reader: it renders events to utterances using the core's speech rendering library and forwards them to a host-provided synthesizer. The rendering library composes announcements from the golden six in NVDA order and handles character echo (punctuation expansion, capital indication), with verbosity as an explicit parameter so a terse and a verbose reader are the same code. Anticipated additional readers include braille, a UIA provider (the retained tree maps directly onto platform accessibility trees), and logging readers for tests.

# 8. Focus and Navigation

Enter and Escape follow the Windows dialog convention: each layer may designate a primary and a cancel widget, Enter activates the primary and Escape the cancel, and only buttons claim Enter for themselves. A primary or cancel widget the user cannot currently reach — disabled, hidden, or inside a hidden subtree — does not activate; the input falls through unconsumed. Non-button widgets never claim Enter — there is no per-widget "item activated" event; acting on a list selection is the primary widget's job (it reads the selection) or that of an explicitly bound command.

Navigation has three axes. The tab ring visits every focusable node in depth-first tree order with wraparound; hidden subtrees and disabled nodes are skipped, and groups and labels are never focusable. Hierarchy navigation moves along the tree structure itself: to parent, first visible child, or previous/next sibling with wraparound. Widget-internal navigation (arrows inside a list or edit box) is the focused widget's own affair and takes precedence through the claim order.

Any widget may carry shortcuts: key combos with a per-shortcut action — jump to the widget, activate it (the program receives an activated event, without a focus move), or both, jump first. A widget may carry any number of shortcuts, and the first one added is the one focus announcements speak (the shortcut slot of the golden six). Shortcuts fire only while their widget is reachable — visible, enabled, outside hidden subtrees — and when several widgets bind the same combo, the first reachable claimant in depth-first tree order wins. Mnemonics (Alt plus a letter) are the conventional jump-action case; the framework treats them as ordinary shortcuts on the Alt+letter combo.

Focus memory remembers the last-focused child of each container so re-entering a container restores position. When the focused node is removed, focus recovers to the nearest surviving focusable node; when a layer is popped, the previous layer's focus is restored. Focus changes emit a focused accessibility event exactly once per settled change.

# 9. Commands and Key Bindings

Key bindings are host policy. The core stores no command maps and dispatches no commands: input the claim order leaves unconsumed returns from `handle_input` to the host, which matches it against whatever binding scheme it likes — as do command palettes, categories, jump lists, and persistence.

The core contributes three mechanisms to that host machinery. `reserved_reason` names the combos the framework itself consumes — plain Tab and Shift+Tab (the focus ring), Alt+letter (mnemonics), Alt+arrows (hierarchy navigation) — each with a spoken refusal for bind dialogs; everything else, including Ctrl+Tab and Escape, is bindable. `Role::reserves_key` reports which keys a widget role consumes during normal interaction, so a bind dialog can warn about combos the focused widget would swallow. And the shortcut field captures any bindable combo before the framework can interpret it — including Alt+arrows and mnemonics — deliberately releasing only Tab and Escape so the keyboard user can always leave the field.

# 10. FFI and the C# Binding

The C ABI follows a small set of rules. All objects are opaque handles: contexts are pointers, nodes are the `u64` form of `NodeId` with zero meaning "no node". All strings are UTF-8; strings returned by the library are released through `srui_string_free`. There are no callbacks; the host polls, and events cross the boundary through drain calls returning flat struct arrays. Logical inputs cross as a flat (kind, char, key, modifiers) encoding with a round-trip test on the Rust side. The ABI is hand-designed and the C# P/Invoke layer hand-written (all booleans marshal as one byte, matching Rust's `bool`).

Accessibility events currently cross the boundary pre-rendered: each carries its utterance (composed by `speech::render_event`) plus the event variant and node, which is what a speech reader needs. Full structured payload marshaling for braille/UIA readers over FFI is deliberately deferred until such a reader exists.

The core is not thread-safe and does not need to be: one UI context belongs to one thread, and each binding enforces single-threaded access to a context. Multiple contexts on different threads are fine.

The polling loop is allocation-free at idle on both sides of the boundary. An empty drain or pump allocates nothing in Rust (an empty boxed slice has no backing storage), and the C# wrappers return a shared empty batch rather than a fresh list, so an idle application's memory stays flat instead of accruing garbage at the loop cadence. Batches returned by `SdlHost.Pump` and `Ui.Drain` are therefore read-only by contract.

# 11. Testing

The entire core runs headless. Invariants are property-tested, including: the tab ring lands only on valid focusable nodes and visits each exactly once per cycle; announcement composition always contains name and role in order; binding maps keep forward and reverse views consistent. Speech output is asserted as strings through test readers. New behavior is expected to arrive with its invariants encoded the same way.
