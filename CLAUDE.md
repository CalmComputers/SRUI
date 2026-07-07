# SRUI Project Guide

# 1. What This Is

SRUI is a screen-reader-first UI toolkit: a retained semantic tree in Rust, keyboard input in, structured accessibility events out, no pixels ever. The design is specified in docs/architecture.md — read it before changing behavior, and keep it current when behavior changes; it is the source of truth, not a summary. This file covers only what that document does not: mechanics, conventions, and the current state of the C# surface.

# 2. Layout

Rust workspace at the root; .NET projects under dotnet/ (solution: dotnet/Srui.slnx).

- srui-core — the engine. Pure and headless; all widget behavior lives here.
- srui-sdl — SDL3 host layer (window, event pump, key translation).
- srui-prism / srui-prism-sys — speech/braille via vendored Prism (CMake build).
- srui-ffi — the C ABI cdylib (srui_ffi.dll); one flat surface over core, SDL host, and Prism speech.
- srui-demo — Rust end-to-end demo.
- srui-audio-native — cosmos.dll: vendored miniaudio + cosmos DSP nodes + Steam Audio glue, C sources in csrc/, phonon binaries in phonon/.
- dotnet/Srui.Net — hand-written P/Invoke binding plus the class-based widget layer (SruiApp, Widget subclasses, Dialog, SruiDialogs).
- dotnet/Srui.Audio — game audio over cosmos.dll: Sound, SoundGroup buses with effect chains, HRTF pooling, tweens.
- dotnet/SruiDemo — C# end-to-end demo: a tab-switched widget gallery exercising every widget type, all canned and hand-built dialogs, dynamic state, the event stream (with a reviewable event log on Ctrl+L), and sound-augmented lists over Srui.Audio.
- dotnet/AudioExample — Srui.Audio walkthrough (no UI stack; needs only cosmos.dll and phonon.dll).

# 3. Building and Running

Native first, managed second. `cargo build` produces everything the C# side needs in target/debug: srui_ffi.dll, prism.dll, SDL3.dll, cosmos.dll, phonon.dll. The csproj files copy those DLLs from target/debug (or target/release when built with `-c Release` — the cargo profile must match the .NET configuration) into the .NET output directory.

- Rust demo: `cargo run -p srui-demo`
- C# demo: `dotnet run --project dotnet/SruiDemo`
- Audio walkthrough: `dotnet run --project dotnet/AudioExample`

The demos speak through Prism (the running screen reader, or platform TTS as fallback) and need a real window with keyboard focus, so they are not useful headless.

# 4. Testing

All behavioral tests are Rust-side: `cargo test`. The core runs fully headless; invariants are property-tested and speech output is asserted as strings through test readers (architecture.md, section 11). New core behavior is expected to arrive with its invariants encoded the same way.

There is no C# test project. Srui.Net is a thin wrapper whose behavior lives in the core, so it is verified through the demos; if a C#-side bug class ever needs regression coverage, raise that rather than silently adding a harness.

# 5. Rules

- Bindings never reimplement behavior. If C# needs something the core can do, extend srui-ffi and wrap it; do not simulate it in C#. The exceptions are deliberate: dialog conventions and canned dialogs are binding-level policy (architecture.md, sections 4.4 and 10), and Srui.Audio is C#-only orchestration over native DSP.
- The C ABI is hand-designed and the P/Invoke layer hand-written (NativeMethods.cs). Opaque handles, UTF-8 strings freed via srui_string_free (the TakeString helper), booleans marshaled as one byte, no callbacks — the host polls and drains. Keep new entry points consistent with these rules and with the flat (kind, char, key, mods) input encoding.
- One Ui, one thread. Nothing in the binding may introduce cross-thread access to a context.
- Steady-state audio paths (Tick, SetListener, spatialization updates) allocate nothing; keep them that way.
- srui-demo is the minimal Rust end-to-end check; dotnet/SruiDemo is the broader gallery. A core behavior change that alters user-visible UX should be reflected in both where both exercise it.
- Widget wrappers are designed to be subclassed from application assemblies: override the On* methods and keep the base call so composition subscribers still fire. Cross-assembly quirk: Widget's On* members are protected internal, but an override outside Srui.Net declares plain protected.
- Vendored code (srui-prism-sys/prism, srui-audio-native/csrc and phonon) is snapshot plus documented local patches. Any change to vendored sources must be recorded in that crate's PATCHES.md so it survives a snapshot update; prefer additive wrapper files (the cosmos_extra.c pattern) over edits to upstream files.
- Docs are Wikipedia-esque prose with numbered headings and no historical narrative; history belongs in commit messages. docs/architecture.md must always describe the present design.

# 6. The C# Surface As Of Now

Everything in the core's widget set is usable from C#: all built-in roles (label, group, button, checkbox, edit box single/multi-line with read-only, listbox, filter listbox, slider, tab control, shortcut field, role-less custom widget), layers/dialogs, primary/cancel routing, tickers, focus control, announcements, hidden/disabled/name/description mutation, widget shortcuts (Widget.AddShortcut with a config-form combo string and a jump/activate/both action), and the host clipboard. Game-style input rides the physical key stream (architecture.md, section 6.4): Widget.BindKey attaches press/repeat/release handlers to any widget, focus-scoped, with SruiApp.UnhandledKey and SruiApp.FocusLost as the app-level hooks. Accessibility events cross the boundary pre-rendered to utterances, which is exactly what the self-voicing path needs.

The boundary's current limits, all deliberate and documented in architecture.md section 10 unless noted:

- Custom widget behavior is Rust-only, permanently. C# extends by subclassing wrappers or composing.
- Structured event payloads do not cross FFI yet (deferred until a braille/UIA reader exists), and speech verbosity is not host-configurable over FFI.
- Some programmatic setters exist in the core but are not yet exported: list selection, active tab, shortcut-field combo, filter items. Reads exist for all of them; add the export when a consumer appears.
- The key-combo display/spoken forms, reserved_reason, and Role::reserves_key are not exposed, so a C# bind dialog cannot yet do conflict detection. Config-string parsing is exposed (Keys.TryParse; AddShortcut and BindKey parse internally).
