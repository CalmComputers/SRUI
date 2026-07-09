# HelloSrui

# 1. What This Is

A minimal application that consumes srui as a set of compiled binaries — no project references, no source tree, exactly what a closed-source consumer sees. It shows the full stack: a window with speaking widgets (Srui.Net over the native engine) and positional audio with a bus effect (Srui.Audio over cosmos). The project is deliberately not part of dotnet/Srui.slnx.

# 2. Producing the Binary Drop

From the repository root, run `./dist.ps1`. It builds everything in Release and collects the drop into `dist/`:

| File | Component | Needed by |
|---|---|---|
| Srui.Net.dll | The engine: widget classes, app shell, SDL host, speech | UI |
| prism.dll | Speech output via screen reader or platform TTS | UI (native) |
| SDL3.dll | Window and keyboard input | UI (native) |
| Srui.Audio.dll | Sounds, groups, effects, tweens | audio |
| cosmos.dll | Audio engine and DSP (miniaudio + cosmos nodes) | audio (native) |
| phonon.dll | Steam Audio HRTF | audio (native) |

A UI-only application ships the first three; an audio-only one ships the last three.

# 3. Wiring a Project

Three things, all visible in HelloSrui.csproj:

- Reference the managed assemblies as plain DLLs (`<Reference>` with a `<HintPath>`). The `SruiDist` property holds the drop's location; repoint it for your layout.
- Copy the native DLLs beside the exe (`<None>` items with `CopyToOutputDirectory`). They are loaded by name from the application directory.
- Target `net10.0`.

Two runtime rules carry over from srui itself: one SruiApp belongs to one thread, and the app needs a real window with keyboard focus (speech goes through the running screen reader, or platform TTS as fallback), so it is not useful headless.

Audio comes from `app.Audio`: an app-owned SoundManager whose automation (pitch tweens, spatialization refresh) the event loop advances itself, about every 5 ms at idle. Only a consumer using Srui.Audio without SruiApp calls `SoundManager.Tick` from its own loop (dotnet/AudioExample in the srui source shows that pattern). A UI-only application that never touches `app.Audio` can omit Srui.Audio.dll, cosmos.dll, and phonon.dll — the assembly loads lazily.

# 4. Running

```
./dist.ps1                                  # once, from the repository root
dotnet run --project samples/HelloSrui
```

Tab and Shift+Tab move focus. Arrow the Position slider to hear the ping slide across the stereo field (HRTF when Steam Audio is available); toggle Reverb with Space to put a room on the bus; Enter anywhere (or Ctrl+G, a widget shortcut) greets by the typed name; Escape quits.
