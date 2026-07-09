# SRUI Architecture

# 1. Overview

SRUI is a screen-reader-first UI toolkit for .NET. It maintains a retained semantic tree of widgets, processes keyboard input against that tree, and emits structured accessibility events describing what the user should perceive. It produces no pixels and never will: visual rendering is permanently out of scope. What an application does with the events — synthesize speech, drive a braille display, back a platform accessibility API — is decided by the readers attached to it (section 7).

The toolkit is pure C# from the application surface down to the engine. Native code appears only at the platform edges — SDL3 for the window and keyboard, Prism for speech, cosmos/phonon for game audio — each reached through hand-written P/Invoke (section 10).

# 2. Design Principles

## 2.1. Sans-IO engine

The engine owns no platform resources: no TTS engine, no clipboard, no window, no event loop, no file access, no wall clock (time is injected). The host pushes input in and drains events out. Platform services the engine needs (clipboard) are injected behind interfaces. This keeps the engine headless and deterministic under test — a headless `SruiApp` is a complete, drivable application with no native dependencies.

## 2.2. One object per widget

A widget is a single object: the application-facing surface, the widget's state, and its input behavior live together in a `Widget` subclass, and that object is its node in the semantic tree. Identity on the public surface is the object reference — there are no handles to go stale, and events reach their widget directly. The node's label (the golden six, section 4.2) is a projection the widget keeps in sync for announcements; widget state (an edit box's text, a list's selection) is read and mutated through the widget's own properties, and user-driven changes are reported through its events. Application state synchronization is the program's responsibility (a future reactive layer may automate it).

## 2.3. Structured events out; readers render

The engine never speaks. It emits structured accessibility events carrying complete semantic payloads (`AccessibilityEvent`, carrying widget references and label snapshots). Speech composition is provided as a pure library (`SpeechRenderer`: event in, utterance string out) that readers may use or bypass. This is what lets one event stream feed a self-voicing reader, a braille reader, a UIA provider, and a test harness simultaneously.

## 2.4. Policy-free

Application vocabulary stays out of the engine: no application command names, no command categories, no persistence formats, no application-global shortcuts. The engine supplies mechanisms (key combos, binding maps, conflict detection) and the host supplies policy. Serialization of user configuration is host-owned; the engine exposes stable string forms (for example `"ctrl+shift+s"` for key combos) and plain data structures.

# 3. Layering

| Layer | Where | Role |
|---|---|---|
| Engine | `Srui.Net`, namespace `Srui.Core` (internal) | Retained tree, focus/navigation, input dispatch, the output event queue and coalescing, the text engine (rope, grapheme/word/line navigation, editor). Pure; no platform dependencies; no widget behavior of its own. |
| Widget layer | `Srui.Net`, namespace `Srui` | The public API: `SruiApp` (engine ownership, readers, dialogs, the loop), the `Widget` base class and the built-in widgets (each carrying its own behavior), `Dialog` and the canned dialogs, `Ticker`, the input and event vocabulary (`InputEvent`, `AccessibilityEvent`, `Key`/`KeyCombo`). |
| Host | `Srui.Net` (`SdlHost`) | SDL3 window and event pump: a focus-receiving window, physical→logical input translation, the physical key stream (section 6.4), and the system clipboard. Optional — a headless `SruiApp` accepts logical inputs from any host. |
| Speech | `Srui.Net` (`Speech`, `SpeechReader`) | Speech output through Prism (vendored under `native/prism`), which routes to the running screen reader or platform TTS. `SpeechReader` is the reference reader; a windowed `SruiApp` installs one out of the box. |
| Game audio | `Srui.Audio` over cosmos.dll | 3D positioning (Horizon-style pan/volume or Steam Audio HRTF), sound groups with a native effect chain (convolution reverb, EQ, filter, distortion, vocoder, disperser, delay), pitch tweens, offline time-stretch. All DSP stays native (cosmos.dll: miniaudio + vendored cosmos nodes, decoding wav, flac, mp3, vorbis, and opus); C# orchestrates with zero steady-state allocation. Everything is edge-triggered except time-based automation, which `SoundManager.Tick` applies: `SruiApp` owns an on-demand manager (`SruiApp.Audio`) and ticks it from the event loop at the loop cadence (about 5 ms at idle); loop-less consumers tick manually. |
| Readers | application-side | Consumers of the accessibility event stream (`IReader`): self-voicing speech, braille, UIA provider, logging/test readers. |

`dotnet/SruiDemo` wires the whole stack together end to end: SDL events in, Prism speech out. A reactive layer over the retained API is anticipated but deferred.

# 4. The Semantic Tree

## 4.1. Nodes and widgets

The tree owns all nodes in a dictionary keyed by an engine-internal handle allocated from a monotonic counter and never reused; resolution is O(1) and a stale handle resolves to nothing rather than to a recycled node. Each node stores its parent, an ordered child list, its semantic label, and a reference to the `Widget` object that embodies it — the object that handles the node's input and receives its events. Handles never cross the public surface: applications hold widget objects, and an event queued for a widget that has since been removed resolves to nothing and is dropped at dispatch.

## 4.2. The golden six

Every node's label carries the six semantic properties a screen reader announces, in NVDA order: name, role, value, states, description, shortcut. The role is carried as its spoken text ("button", "edit read only multi line", or a custom widget's own word like "table"); an empty role text announces role-less — the usual choice for game surfaces whose identity is their name. Which keys a widget consumes during normal interaction — the basis of shortcut conflict detection — is the widget's own declaration (`Widget.ReservesKey`), not a property of the role text. `States` is a bitflag set of conditions the user cannot directly change (disabled, required, warning, hidden), each settable as a widget property. The shortcut slot holds the widget's shortcut list (section 8); announcements speak the first shortcut added, in its spoken display form. A node's name is optional; a nameless node announces as role and value only, for widgets whose identity is carried by their container. Reader-facing events carry the label as an immutable snapshot (`WidgetInfo`) taken at emission time, so readers never race the tree.

## 4.3. Mutation

Programs build UI by constructing widgets (each widget inserts its node under its container at construction) and change it through widget properties. Mutations are synchronous and immediately visible. Mutations that affect what the user is perceiving cause appropriate accessibility events; mutations elsewhere in the tree are silent. A programmatic change to a focused widget's single value state — a slider's value, a list's selection, the active tab — speaks exactly what the equivalent user-driven change speaks (the new value alone, so a ticking progress slider stays terse), because the property setter and the input handler share the emission; other label changes (name, description, role text) re-announce the widget in full. Removing a widget removes its subtree, and focus recovers to the nearest surviving focusable node with an announcement.

## 4.4. Layers

The tree maintains a stack of layers for modal UI. Each layer has its own roots, focus, and default primary/cancel widgets (Enter and Escape targets). Only the top layer is navigable. Pushing a layer opens a dialog or palette; popping it removes the layer's nodes and restores the previous layer's focus. The base layer cannot be popped.

Dialog conventions live in the widget layer, not the engine: there is no dialog role (the prompt is a Label preceding the widgets, announced as a context label via the focused-with-context re-announcement), Escape closes a dialog automatically when no cancel widget claims it, and the canned dialogs (message, confirm, custom button rows, reviewable read-only status text) are `SruiDialogs` helpers. Canned dialog buttons carry the Windows-conventional Alt+letter activation shortcut, assigned first-free-letter within the dialog and spoken with the button; widget shortcuts only match within the active layer, so these never collide with the window underneath.

# 5. Widget Behavior and Authoring

Widget behavior is virtually dispatched on the widget object itself: when input reaches a focused widget, the engine calls its `OnInput` override, which mutates the widget's own state, keeps the label in sync, and emits events. The built-in widgets (button, checkbox, single- and multi-line edit box with read-only, listbox, filter listbox, slider, tab control, shortcut field) are ordinary subclasses of the public `Widget` base and double as the reference examples for the three extension paths:

- **Composition** — subscribe to a widget's events (`Changed`, `Activated`, `Toggled`…).
- **Subclassing a built-in** — override its `On*` methods, keeping the base call so composition subscribers still fire (the demo's sound-augmented lists).
- **Authoring a new widget kind** — subclass `Widget` directly: pass the spoken role text to the base constructor, override `OnInput` to claim input, keep the label current with `SetValue`/`SetStateText`, describe the perceptual result with `Emit`/`EmitItem`/`Announce`, and defer program notifications with `Post`/`NotifyChanged` so handlers never run inside input dispatch. Override `ReservesKey` to declare the combos the widget consumes, and the widget participates in bind-dialog conflict warnings like any built-in. An authored widget is a full citizen of the tab ring, focus recovery, dialog layers, shortcuts, and every attached reader (the demo's table widget is the worked example).

A `CustomWidget` is the degenerate case: focusable, role-less, no behavior — its interaction lives entirely in host bindings over the physical key stream (section 6.4).

The text engine under the edit box deserves note. Text lives in a rope (a balanced tree of string leaves, O(log n) local edits); positions are UTF-16 code units, exposed on the editing surface (`CursorPosition`, `Selection`) with setters that clamp to the text and snap out of surrogate pairs. Cursor movement is grapheme-aware (text-element segmentation, so combining sequences, surrogate pairs, and CRLF move as single characters), and word navigation uses character-class scans in three flavors: code-editor boundaries for speech context, Windows-style word starts for Ctrl+arrows, and Notepad-style word extents for Ctrl+Shift selection and word deletion.

# 6. Input

## 6.1. Vocabulary

A key combo (`KeyCombo`) is a physical key plus ctrl/alt/shift modifier state, with a spoken display form ("control shift s") and a compact config form ("ctrl+shift+s") that round-trips through parsing (`KeyCombo.TryParseConfig`; the flat-encoded `Keys.TryParse` serves the physical stream). A logical input (`InputEvent`, an `InputKind` plus payload) is the semantic layer above it: navigation (next/previous, hierarchy moves, mnemonic), widget verbs (activate, move by character/word/line, select, type, delete, clipboard), `SpeakFocus`, `Dismiss`, and `RawKey` for combos with no semantic mapping. The default physical-to-logical map lives in the SDL host and is host-replaceable; the host is responsible for translating platform key events into logical inputs.

## 6.2. Dispatch

Input events are pushed to the app one at a time (`SruiApp.HandleInput`); there is no frame granularity. Each event is dispatched in a fixed claim order: the focused widget's `OnInput` gets first claim, then framework navigation and layer defaults (tab ring, hierarchy navigation, primary/cancel), then widget shortcuts (section 8), then dialog dismissal, then the host's own bindings (`UnhandledInput`). Whatever handles the event emits the corresponding output events; queued output is delivered when the app drains (`DispatchEvents` — the event loop does this every iteration).

## 6.3. Time

The engine has no clock; the host feeds one through `SetNow` (monotonic milliseconds) — the event loop does this every iteration, and headless hosts do it themselves. Typeahead timeouts and tickers are both observed there. A ticker (`SruiApp.StartTicker`) raises its `Tick` event each time its interval elapses, at `SetNow` resolution, drift-tolerant: a late check fires once rather than bursting to catch up.

## 6.4. The physical key stream

Alongside the logical stream, the host layer reports raw key transitions for game-style input: the initial press, auto-repeats, and the release of every key with a key-combo form (bare modifier taps have none). This stream never enters the engine — the engine is semantic, and key bindings are host policy (section 9) — and it is independent of the claim order: a press is reported even when the corresponding logical input is consumed. Because a release can never be consumed and a press can never be hidden, the pairing a game relies on is unconditional. The host also signals the window losing keyboard focus, which orphans held keys: game state that tracks them must be zeroed, as the releases will never arrive.

The widget layer builds per-widget key bindings on this stream: handlers attach to any widget for a combo and phase (press, repeat, release) and fire while that widget is focused (`Widget.BindKey`), with unclaimed transitions falling to an app-level hook (`UnhandledKey`). Press and repeat match the exact combo; release matches the key alone, so a modifier pressed mid-hold cannot orphan the release.

# 7. Output Events and Readers

## 7.1. Event streams

The engine produces a single ordered stream of output events. Accessibility events (`AccessibilityEvent`, public) describe perception: focus moved (with the widget, a golden-six snapshot, and any context labels), text typed or deleted, cursor moved, selection changed, list item changed (with position), tab changed, slider changed, filter results changed, boundary hit, plus a free-form announce escape hatch. Every variant except the free-form announcement carries a reference to the widget it concerns. Editor events deliberately carry only perceptual content (the grapheme, the echoed word, the spoken context) — never document text; a reader that needs more queries the widget. A read-only editor swallows typing silently — nothing was inserted, so there is nothing to echo — and lets Enter fall through to the layer's primary widget. Program notifications (a button's activation, a widget's `Changed`) ride the same queue as deferred callbacks and are delivered to the widget's own events at drain time — handlers never run inside input dispatch, so they may freely open dialogs or remove widgets. Tick events report elapsed ticker intervals. Programs react to widget events; readers react to accessibility events.

## 7.2. Draining and coalescing

The app drains the event queue when it chooses (`DispatchEvents`; the event loop drains every iteration, headless hosts after each input or mutation batch). Coalescing is applied at drain time: for state-describing events (focus, selection, item/tab/slider/filter changes), only the latest instance of each event kind survives a drain — an intermediate focus move is discarded in favor of the settled one; transient events (typing echoes, announcements) and all program notifications survive in order. Interruption policy — silencing speech on a keypress, utterance length limits — belongs to the speech reader, not the engine: the app broadcasts `OnInterrupt` to every reader when a key goes down or `AnnounceNow` demands urgency.

## 7.3. Readers

A reader is any consumer of accessibility events: implement `IReader.OnEvent` and attach with `SruiApp.AddReader`; every attached reader hears every event, in order. The reference reader is the self-voicing `SpeechReader`: it renders events to utterances with the `SpeechRenderer` library and forwards them to Prism, and a windowed app installs one automatically. The rendering library composes announcements from the golden six in NVDA order and handles character echo (punctuation expansion, capital indication). Anticipated additional readers include braille, a UIA provider (the retained tree maps directly onto platform accessibility trees), and logging readers — the test suite's recording reader is one.

# 8. Focus and Navigation

Enter and Escape follow the Windows dialog convention: each layer may designate a primary and a cancel widget, Enter activates the primary and Escape the cancel, and only buttons claim Enter for themselves. A primary or cancel widget the user cannot currently reach — disabled, hidden, or inside a hidden subtree — does not activate; the input falls through unconsumed. Non-button widgets never claim Enter — there is no per-widget "item activated" event; acting on a list selection is the primary widget's job (it reads the selection) or that of an explicitly bound command. Activation is a `Widget`-level concept: any widget targeted by primary/cancel routing or an activate shortcut raises its `Activated` event.

Navigation has three axes. The tab ring visits every focusable node in depth-first tree order with wraparound; hidden subtrees and disabled nodes are skipped, and groups and labels are never focusable. Hierarchy navigation moves along the tree structure itself: to parent, first visible child, or previous/next sibling with wraparound. Widget-internal navigation (arrows inside a list or edit box) is the focused widget's own affair and takes precedence through the claim order.

Any widget may carry shortcuts: key combos with a per-shortcut action — jump to the widget, activate it (its `Activated` event fires, without a focus move), or both, jump first. A widget may carry any number of shortcuts, and the first one added is the one focus announcements speak (the shortcut slot of the golden six). Shortcuts fire only while their widget is reachable — visible, enabled, outside hidden subtrees — and when several widgets bind the same combo, the first reachable claimant in depth-first tree order wins. Mnemonics (Alt plus a letter) are the conventional jump-action case; the framework treats them as ordinary shortcuts on the Alt+letter combo.

Focus memory remembers the last-focused child of each container so re-entering a container restores position. When the focused node is removed, focus recovers to the nearest surviving focusable node; when a layer is popped, the previous layer's focus is restored. Focus changes emit a focused accessibility event exactly once per settled change.

# 9. Commands and Key Bindings

Key bindings are host policy. The engine stores no command maps and dispatches no commands: input the claim order leaves unconsumed reaches the app's `UnhandledInput` hook, which matches it against whatever binding scheme the application likes — as do command palettes, categories, jump lists, and persistence.

The toolkit contributes three mechanisms to that host machinery, all public. `KeyCombo.ReservedReason` names the combos the framework itself consumes — plain Tab and Shift+Tab (the focus ring), Alt+letter (mnemonics), Alt+arrows (hierarchy navigation) — each with a spoken refusal for bind dialogs to announce; everything else, including Ctrl+Tab and Escape, is bindable. `Widget.ReservesKey` reports which keys a widget consumes during normal interaction, so a bind dialog can warn about combos the focused widget would swallow — custom-authored widgets participate by overriding it. And the shortcut field captures any bindable combo before the framework can interpret it — including Alt+arrows and mnemonics — deliberately releasing only Tab and Escape so the keyboard user can always leave the field.

# 10. Native Boundaries

Native code sits behind three hand-written P/Invoke surfaces, each over a DLL staged by `native/build-native.ps1`: SDL3 (window, event queue, clipboard), Prism (speech/braille through the running screen reader or platform TTS), and cosmos with phonon beside it (game audio; consumed by `Srui.Audio`). Strings cross these boundaries as UTF-8; native booleans marshal as one byte; there are no callbacks on the UI path — the host polls SDL and drains the engine. Struct layouts and constants are transcribed from the vendored native headers, not from documentation.

The toolkit is not thread-safe and does not need to be: one `SruiApp` belongs to one thread. Multiple apps on different threads are fine.

The polling loop is allocation-free at idle. An empty pump or drain returns a shared empty batch rather than a fresh list, so an idle application's memory stays flat instead of accruing garbage at the loop cadence. Batches returned by `SdlHost.Pump` are therefore read-only by contract.

# 11. Testing

The toolkit runs fully headless; `dotnet/Srui.Net.Tests` (xUnit) is the behavioral safety net. Behavioral tests drive the public surface end to end: build widgets in a headless `SruiApp`, push logical input, and assert what a recording reader hears as rendered utterances. Engine invariants are property-tested with seeded-random generators, including: the tab ring lands only on valid focusable nodes and visits each exactly once per cycle; word-boundary navigation always makes progress and stays in range; the editor's cursor and selection stay within the document under arbitrary operation sequences; the rope agrees with a reference implementation under thousands of random edits. New behavior is expected to arrive with its invariants encoded the same way.
