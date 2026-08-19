# HelloSrui

# 1. What This Is

A minimal application that consumes srui the way any other project would:
two package references and no source tree. It shows the full stack — a
window with speaking widgets (Srui over the native engine) and positional
audio with a bus effect (Srui.Audio over cosmos). The project is
deliberately not part of Srui.slnx, so it exercises the published surface
rather than the internal one.

# 2. Building

The sample resolves srui from the repository's own `artifacts/` directory,
via the `nuget.config` in this folder, so it works before anything is
published:

```
./pack.ps1                                  # once, from the repository root
dotnet run --project samples/HelloSrui
```

To consume the published packages from nuget.org instead, delete
`nuget.config`.

# 3. Wiring A Project

Two things, both visible in HelloSrui.csproj:

- Reference the packages. `Srui.Audio` arrives transitively with `Srui`, and
  is named explicitly here only because this program uses its types
  directly. An application that never touches sound needs just `Srui`.
- Target `net10.0`.

There is no third step. The native binaries — prism and SDL3 for the UI
stack, cosmos and phonon for audio — travel inside the packages under
`runtimes/win-x64/native/`, and the host resolves them by name. Nothing
needs copying beside the executable.

Two runtime rules carry over from srui itself: one SruiApp belongs to one
thread, and the app needs a real window with keyboard focus (speech goes
through the running screen reader, or platform TTS as fallback), so it is
not useful headless.

# 4. Audio

Audio comes from `app.Audio`: an app-owned SoundManager whose automation
(pitch tweens, spatialization refresh) the event loop advances itself,
every `app.LoopWaitMs` milliseconds at idle (default 2). Only a consumer
using Srui.Audio without SruiApp calls `SoundManager.Tick` from its own
loop; AudioExample in the srui source shows that pattern.

Spatialization belongs to a `SoundEntity`, which owns a group; a `Sound`
created against `entity.Group` is heard wherever the entity is placed.

# 5. Running

Tab and Shift+Tab move focus. Arrow the Position slider to hear the ping
slide across the stereo field (HRTF when Steam Audio reports it available);
toggle Reverb with Space to put a room on the bus; Enter anywhere (or
Ctrl+G, a widget shortcut) greets by the typed name; Escape quits.
