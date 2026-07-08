# SRUI Project Guide

# 1. What This Is

SRUI is a screen-reader-first UI toolkit in C#: a retained semantic tree, keyboard input in, structured accessibility events out, no pixels ever. The design is specified in docs/architecture.md — read it before changing behavior, and keep it current when behavior changes; it is the source of truth, not a summary. This file covers only what that document does not: mechanics, conventions, and the current state of the public surface.

# 2. Layout

.NET projects under dotnet/ (solution: dotnet/Srui.slnx); native sources and builds under native/.

- dotnet/Srui.Net — the toolkit. The engine lives in Core/ (namespace Srui.Core, internal): tree, behaviors, focus/navigation, the text engine (rope, TextNav, EditorState), speech rendering, event coalescing, plus the SDL3 and prism P/Invoke layers. The public namespace Srui holds Ui (the handle-addressed engine facade), SruiApp, the Widget wrapper classes, Dialog, SruiDialogs, SdlHost, and Speech. SruiApp owns an on-demand SoundManager (SruiApp.Audio) and ticks it from the event loop.
- dotnet/Srui.Net.Tests — xUnit suite over the engine (InternalsVisibleTo). Headless; no native DLLs needed.
- dotnet/Srui.Audio — game audio over cosmos.dll: Sound, SoundGroup buses with effect chains, HRTF pooling, tweens. Standalone consumers (no SruiApp) drive SoundManager.Tick from their own loop.
- dotnet/SruiDemo — end-to-end demo: a tab-switched widget gallery exercising every widget type, all canned and hand-built dialogs, dynamic state, the event stream (with a reviewable event log on Ctrl+L), and sound-augmented lists over Srui.Audio.
- dotnet/AudioExample — Srui.Audio walkthrough (no UI stack; needs only cosmos.dll and phonon.dll).
- native/ — everything native: cosmos/ (vendored miniaudio + cosmos DSP nodes + Steam Audio glue + the xiph opus stack; decodes wav, flac, mp3, vorbis, and opus, UTF-8 filenames throughout), phonon/ (Steam Audio binaries), prism/ (vendored Prism), prebuilt/SDL3.dll, and build-native.ps1 which stages every DLL into native/out/.
- samples/HelloSrui — consuming srui as compiled binaries (the dist.ps1 drop), deliberately outside the solution.
- The tag rust-era marks the last commit of the retired Rust implementation the engine was ported from.

# 3. Building and Running

Native first, managed second — but the native step is rare: run native/build-native.ps1 when native sources change or on a fresh clone; it stages prism.dll, SDL3.dll, cosmos.dll, and phonon.dll into native/out/, from which the csproj files copy them. The DLLs are optimized C/C++ regardless of the .NET configuration. The script's toolchain paths (cmake, ninja, clang-cl, vcvars, midl) are overridable via env vars documented at its top.

- C# demo: `dotnet run --project dotnet/SruiDemo`
- Audio walkthrough: `dotnet run --project dotnet/AudioExample`
- Binary drop for external consumers: `./dist.ps1` (see samples/HelloSrui)

The demo speaks through Prism (the running screen reader, or platform TTS as fallback) and needs a real window with keyboard focus, so it is not useful headless.

# 4. Testing

`dotnet test dotnet/Srui.Net.Tests` — the engine runs fully headless. Invariants are property-tested with seeded-random generators, and speech output is asserted as strings by driving logical input into a CoreUi and checking the rendered utterances (architecture.md, section 11). New engine behavior is expected to arrive with its invariants encoded the same way. SruiDemo is the end-to-end check for anything the headless suite cannot see (real window, real speech).

# 5. Rules

- One Ui, one thread. Nothing may introduce cross-thread access to a Ui or its engine.
- Steady-state audio paths (Tick, SetListener, spatialization updates) allocate nothing; keep them that way. The same holds for the idle event loop: empty SdlHost.Pump and Ui.Drain batches are a shared read-only list, so an idle app allocates nothing per iteration.
- The engine (Srui.Core) is sans-IO: no platform calls, no clock, no clipboard — hosts inject those. P/Invoke lives only in the designated interop files (Sdl3.cs, PrismNative.cs, and Srui.Audio's bindings); strings cross as UTF-8, native booleans marshal as one byte, and constants/struct layouts are transcribed from the vendored native headers, not from documentation.
- Widget wrappers are designed to be subclassed from application assemblies: override the On* methods and keep the base call so composition subscribers still fire. Cross-assembly quirk: Widget's On* members are protected internal, but an override outside Srui.Net declares plain protected.
- Behavior classes (Srui.Core.WidgetBehavior and its subclasses) are internal. Applications extend by subclassing wrappers or composing; exposing behavior authoring is anticipated but deliberately deferred, so raise it rather than making one-off things public.
- Vendored code (native/cosmos, native/phonon, native/prism) is snapshot plus documented local patches. Any change to vendored sources must be recorded in native/PATCHES-cosmos.md or native/PATCHES-prism.md so it survives a snapshot update; prefer additive wrapper files (the cosmos_extra.c pattern) over edits to upstream files.
- A behavior change that alters user-visible UX should be reflected in SruiDemo where the demo exercises it.
- Docs are Wikipedia-esque prose with numbered headings and no historical narrative; history belongs in commit messages. docs/architecture.md must always describe the present design.

# 6. The Public Surface As Of Now

Everything in the engine's widget set is usable: all built-in roles (label, group, button, checkbox, edit box single/multi-line with read-only, listbox, filter listbox, slider, tab control, shortcut field, role-less custom widget), layers/dialogs, primary/cancel routing, tickers, focus control, announcements, hidden/disabled/name/description mutation, widget shortcuts (Widget.AddShortcut with a config-form combo string and a jump/activate/both action), and the host clipboard. Game-style input rides the physical key stream (architecture.md, section 6.4): Widget.BindKey attaches press/repeat/release handlers to any widget, focus-scoped, with SruiApp.UnhandledKey and SruiApp.FocusLost as the app-level hooks. Typing, list typeahead, and filter queries are astral-plane-safe (surrogate pairs are one character throughout).

Known deliberate limits:

- Accessibility events reach the public surface pre-rendered to utterances (OutputEvent.Speech). The structured payloads exist in the engine; exposing them awaits the first braille/UIA reader. Speech verbosity is likewise not yet host-configurable.
- Some programmatic setters exist in the engine but are not surfaced on Ui: list selection, active tab, shortcut-field combo, filter items, filter clearing. Reads exist for all of them; surface the setter when a consumer appears.
- The key-combo display/spoken forms, reserved-combo reasons, and Role.ReservesKey are engine-internal, so an app-side bind dialog cannot yet do conflict detection. Config-string parsing is public (Keys.TryParse; AddShortcut and BindKey parse internally).
