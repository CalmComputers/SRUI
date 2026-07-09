# SRUI Project Guide

# 1. What This Is

SRUI is a screen-reader-first UI toolkit in C#: a retained semantic tree, keyboard input in, structured accessibility events out, no pixels ever. The design is specified in docs/architecture.md — read it before changing behavior, and keep it current when behavior changes; it is the source of truth, not a summary. This file covers only what that document does not: mechanics, conventions, and the current state of the public surface.

# 2. Layout

.NET projects under dotnet/ (solution: dotnet/Srui.slnx); native sources and builds under native/.

- dotnet/Srui.Net — the toolkit. The engine lives in Core/ (namespace Srui.Core, internal): tree, focus/navigation, event queue and coalescing, the text engine (rope, TextNav, EditorState), plus the SDL3 and prism P/Invoke layers. The public namespace Srui holds SruiApp, the Widget base class and built-in widgets (one file per major widget; each widget object IS its node and carries its own behavior), Dialog, SruiDialogs, SdlHost, Speech and SpeechReader, and the public vocabulary (InputEvent, AccessibilityEvent, WidgetInfo, Key/KeyCombo, IReader, IClipboard). SruiApp owns an on-demand SoundManager (SruiApp.Audio) and ticks it from the event loop.
- dotnet/Srui.Net.Tests — xUnit suite. Behavioral tests drive the public surface (headless SruiApp + recording reader, asserting rendered utterances); engine invariants (rope, text navigation, tree, tab ring) are property-tested through internals (InternalsVisibleTo). Headless; no native DLLs needed.
- dotnet/Srui.Audio — game audio over cosmos.dll: Sound, SoundGroup buses with effect chains, HRTF pooling, tweens. Standalone consumers (no SruiApp) drive SoundManager.Tick from their own loop.
- dotnet/SruiDemo — end-to-end demo: a tab-switched widget gallery exercising every widget type, a custom-authored table widget (TableWidget.cs — the reference for behavior authoring from outside the toolkit), all canned and hand-built dialogs, dynamic state, the event stream (with a reviewable event log on Ctrl+L), and sound-augmented lists over Srui.Audio.
- dotnet/AudioExample — Srui.Audio walkthrough (no UI stack; needs only cosmos.dll and phonon.dll).
- native/ — everything native: cosmos/ (vendored miniaudio + cosmos DSP nodes + Steam Audio glue + the xiph opus stack; decodes wav, flac, mp3, vorbis, and opus, UTF-8 filenames throughout), phonon/ (Steam Audio binaries), prism/ (vendored Prism), prebuilt/SDL3.dll, and build-native.ps1 which stages every DLL into native/out/.
- samples/HelloSrui — consuming srui as compiled binaries (the dist.ps1 drop), deliberately outside the solution.
- The tag rust-era marks the last commit of the retired Rust implementation the engine was ported from; the tag handle-era marks the last commit of the handle-addressed public API (NodeId + Ui facade + wrapper registry) before widgets became their own nodes.

# 3. Building and Running

Native first, managed second — but the native step is rare: run native/build-native.ps1 when native sources change or on a fresh clone; it stages prism.dll, SDL3.dll, cosmos.dll, and phonon.dll into native/out/, from which the csproj files copy them. The DLLs are optimized C/C++ regardless of the .NET configuration. The script's toolchain paths (cmake, ninja, clang-cl, vcvars, midl) are overridable via env vars documented at its top.

- C# demo: `dotnet run --project dotnet/SruiDemo`
- Audio walkthrough: `dotnet run --project dotnet/AudioExample`
- Binary drop for external consumers: `./dist.ps1` (see samples/HelloSrui)

The demo speaks through Prism (the running screen reader, or platform TTS as fallback) and needs a real window with keyboard focus, so it is not useful headless.

# 4. Testing

`dotnet test dotnet/Srui.Net.Tests` — everything runs fully headless. Behavioral tests build widgets in a headless SruiApp (SruiApp.Headless), push logical input through app.HandleInput, and assert the utterances a recording reader hears (SurfaceTests.cs holds the harness: TestUi/TestReader). Engine invariants are property-tested with seeded-random generators. New behavior is expected to arrive with its invariants encoded the same way. SruiDemo is the end-to-end check for anything the headless suite cannot see (real window, real speech).

# 5. Rules

- One SruiApp, one thread. Nothing may introduce cross-thread access to an app or its engine.
- Steady-state audio paths (Tick, SetListener, spatialization updates) allocate nothing; keep them that way. The same holds for the idle event loop: empty SdlHost.Pump and engine drain batches are a shared read-only list, so an idle app allocates nothing per iteration.
- The engine (Srui.Core) is sans-IO: no platform calls, no clock, no clipboard — hosts inject those. P/Invoke lives only in the designated interop files (Sdl3.cs, PrismNative.cs, and Srui.Audio's bindings); strings cross as UTF-8, native booleans marshal as one byte, and constants/struct layouts are transcribed from the vendored native headers, not from documentation.
- Behavior lives in the widget object. Engine dispatch calls the focused widget's OnInput; the engine itself owns only tree, focus, navigation, shortcuts, layers, coalescing, and the text primitives. Don't put widget-kind knowledge back in the engine.
- Inside OnInput, mutate only your own widget's state and label; anything that touches other widgets or the tree (opening dialogs, removing widgets) must be deferred via Post/NotifyChanged so it runs at drain time, outside dispatch. The built-ins model this.
- Widgets are designed to be subclassed from application assemblies: override the On* methods and keep the base call so composition subscribers still fire; author new widget kinds from the Widget base (SruiDemo's TableWidget is the reference). A programmatic state setter must speak exactly what the equivalent user-driven change speaks — share the emission path, and keep it silent when the widget isn't focused.
- Vendored code (native/cosmos, native/phonon, native/prism) is snapshot plus documented local patches. Any change to vendored sources must be recorded in native/PATCHES-cosmos.md or native/PATCHES-prism.md so it survives a snapshot update; prefer additive wrapper files (the cosmos_extra.c pattern) over edits to upstream files.
- A behavior change that alters user-visible UX should be reflected in SruiDemo where the demo exercises it.
- Docs are Wikipedia-esque prose with numbered headings and no historical narrative; history belongs in commit messages. docs/architecture.md must always describe the present design.

# 6. The Public Surface As Of Now

One object per widget: SruiApp and the Widget classes are the whole public story (no handles; the engine facade is internal). Everything in the widget set is usable: all built-in kinds (Label, Group, Button, CheckBox, EditBox single/multi-line with read-only, ListBox, FilterListBox, Slider, TabControl, ShortcutField, role-less CustomWidget), behavior authoring by subclassing Widget (OnInput, role text, SetValue/SetStateText, Emit/EmitItem/Announce, Post/NotifyChanged, ReservesKey), layers/dialogs, primary/cancel routing (Activated is a Widget-level event, so any widget can be a target), tickers, focus control, announcements, and the host clipboard (injectable on headless apps via SruiApp.SetClipboard). Widget state is properties throughout: Name, Description, Hidden, Disabled, Required, Warning, ListBox.Items/SelectedIndex, TabControl.ActiveIndex, Slider.Value, ShortcutField.Combo (a KeyCombo?), CheckBox.Checked, EditBox.Text/ReadOnly plus an editing surface (CursorPosition, Selection, SelectedText, SelectAll, Length; UTF-16 code-unit positions, setters clamp and snap off surrogate halves). Focused-state setters speak like the equivalent user action.

Readers: structured accessibility events (AccessibilityEvent with Widget references and WidgetInfo snapshots) reach any number of attached IReader implementations; SpeechReader (Prism) is installed by the windowed constructor, and SruiApp.Headless() plus HandleInput/HandleKey/DispatchEvents/SetNow make any host loop (or test) a first-class driver. SpeechRenderer is the public reference rendering.

The bind-dialog toolkit is complete: KeyCombo is public with display/config forms, TryParseConfig, and ReservedReason (framework-reserved combos with spoken refusals); Widget.ReservesKey is a public virtual every widget kind implements.

Game-style input rides the physical key stream (architecture.md, section 6.4): Widget.BindKey attaches press/repeat/release handlers to any widget, focus-scoped, with SruiApp.UnhandledKey and SruiApp.FocusLost as the app-level hooks. Typing, list typeahead, and filter queries are astral-plane-safe (surrogate pairs are one character throughout).

Known deliberate limits:

- Speech verbosity is not yet reader-configurable; SpeechRenderer has a single rendering. The parameter's home is SpeechReader when it comes.
- No braille or platform (UIA) reader ships yet; the reader seam is where they attach.
- Widget shortcut lists have no public getter (announcements speak the first shortcut; a bind dialog that wants to enumerate them will motivate the accessor).
- Tree insertion is append-only on the public surface (the engine supports indexed insertion; surface it when a consumer appears).
