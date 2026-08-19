# SRUI

A screen-reader-first UI toolkit for C#. SRUI keeps a retained tree of
semantic widgets, takes keyboard input, and emits structured accessibility
events. It draws nothing: there are no pixels, no layout, and no rendering
pass. What a user perceives is speech, and what a program manipulates is a
tree of labelled widgets with roles, states, and values.

It is the toolkit behind Lightspeed and Blindatro.

# 1. Requirements

- .NET 10 or later.
- Windows on x64. The native binaries are published for that platform only;
  a build for any other target warns (SRUI0001) and fails at run time.
- The Microsoft Visual C++ redistributable, which `SDL3.dll` and
  `cosmos.dll` both import.
- For speech, a running screen reader. Prism routes to whichever is present
  and falls back to the platform's own text-to-speech.

An application does not need a screen reader to *run* — the toolkit is
fully headless-testable — but it needs one to be heard.

# 2. Installation

```
dotnet add package Srui
```

The native binaries travel inside the package and are found automatically;
nothing needs copying beside the executable.

`Srui.Audio` comes with it as a dependency. It can also be used on its own,
without the UI stack, for programs that only want sound:

```
dotnet add package Srui.Audio
```

# 3. A First Program

```csharp
using Srui;

using var app = new SruiApp("Greeter");

new Label(app, "Greeter");
var name = new EditBox(app, "Your name");
var greet = new Button(app, "Greet");
var quit = new Button(app, "Quit");

// Enter anywhere presses Greet; Escape anywhere presses Quit.
app.SetPrimary(greet);
app.SetCancel(quit);

greet.Activated += () =>
    app.Announce($"Hello, {(name.Text.Length == 0 ? "stranger" : name.Text)}.");
quit.Activated += app.Quit;

app.Run();
```

Tab and Shift+Tab move between the widgets, and each announces itself on
arrival: "Your name edit blank", "Greet button". Typing in the edit box
echoes characters; Enter greets from anywhere in the window.

# 4. Testing What It Says

The spoken surface is the product, so it is the thing worth asserting on. A
headless application takes logical input directly and reports what a reader
would have heard, with no window and no native binaries involved:

```csharp
using Srui;

var app = SruiApp.Headless();
var recorder = new Recorder();
app.AddReader(recorder);

var greet = new Button(app, "Greet");
greet.Activated += () => app.Announce("Hello, stranger.");

greet.Focus();
app.HandleInput(InputEvent.Simple(InputKind.Activate));
app.DispatchEvents();

// recorder.Spoken is now ["Greet button", "Hello, stranger."]

sealed class Recorder : IReader
{
    public readonly List<string> Spoken = new();

    public void OnEvent(AccessibilityEvent e)
    {
        if (SpeechRenderer.RenderEvent(e) is { } utterance) Spoken.Add(utterance);
    }

    public void OnInterrupt() { }
}
```

Output is coalesced, so a run that pushes several inputs before dispatching
sees only the final state of what they changed. Dispatch between steps when
the intermediate utterances are the point.

This is how SRUI tests itself; `Srui.Tests/SurfaceTests.cs` holds the
reference harness.

# 5. What Is In The Packages

| Package | Contents | Native binaries |
|---|---|---|
| Srui | Widgets, dialogs, focus and navigation, shortcuts, the text engine, the SDL host, speech | prism.dll, SDL3.dll |
| Srui.Audio | Sounds, buses, effect chains, HRTF spatialisation, tweens | cosmos.dll, phonon.dll |

Steam Audio (`phonon.dll`) is not optional: `cosmos.dll` imports it
directly, so it ships wherever the audio package does.

# 6. Documentation

- `docs/architecture.md` — the design, and the source of truth for
  behaviour.
- `docs/accessibility-guidelines.md` — how to decide what an interface
  should say.
- `docs/shortcut-geometry.md` — keyboard layout conventions.
- `samples/HelloSrui` — the smallest complete application, consuming the
  published packages.

The repository also carries four runnable demonstrations: `SruiDemo` (every
widget kind), `SruiTasks` (an application structured around subclassed
behaviour), `MultiAppDemo` (several applications in one window), and
`AudioExample` (the audio stack alone).

# 7. Building From Source

Native first, managed second:

```
./native/build-native.ps1     # only on a fresh clone, or when native sources change
dotnet build Srui.slnx
dotnet test Srui.Tests
```

`./pack.ps1` produces the NuGet packages; `./dist.ps1` produces a flat
directory of binaries for consumers who do not use NuGet.

# 8. Licence

SRUI is licensed under Apache-2.0; see `LICENSE`. The bundled native
libraries carry their own terms, recorded in `THIRD-PARTY-NOTICES.md`.
