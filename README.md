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

A new project can start from the template pack instead:

```
dotnet new install Srui.Templates
dotnet new srui-app -n MyThing
```

which creates a windowed application (a name field and a greet button)
and a headless test project whose tests pass from the first build.

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

The spoken surface is the product, so it is the thing worth asserting on.
The `Srui.Testing` package drives a headless application and asserts what a
reader would have heard, with no window and no native binaries involved:

```
dotnet add package Srui.Testing
```

```csharp
using Srui;
using Srui.Testing;

using var ui = new TestApp(app =>
{
    var greet = new Button(app, "Greet");
    greet.Activated += () => app.Announce("Hello, stranger.");
});

ui.Expect("Greet button");   // focus landed on the button and announced it
ui.Press("enter");
ui.Expect("Hello, stranger.");
```

`Press` simulates a key tap the way a real host delivers it — physical key
phases, the standard key mapping, typed characters — and `Wait` advances
the clock, so timeouts and tickers elapse instantly. For anything beyond
exact batches, `Spoken()` returns the utterances as a plain list to query
however the test likes. The package depends on no test framework; it works
under xUnit, NUnit, MSTest, or a bare console.

An exchange can also be frozen as a plain-text scenario file and replayed
from a test with `ui.RunScenario(path)`:

```
// the greeting flow
say Greet button
enter
say Hello, stranger.
```

A bare line taps a key combo; `say` asserts the next utterance exactly;
`nospeech` asserts silence; `down`, `up`, `type`, and `wait` cover holds,
text, and time. Speech no line asserts is out of scope and ignored.
`ui.RecordScenario(path)` rewrites a file's assertions from a real run —
record once, read the file to approve it, and review later changes as
diffs. Running the tests with the environment variable `SRUI_RECORD=1`
re-records every scenario they run instead of asserting, which
re-approves a whole suite after an intentional speech change.

This is how SRUI tests itself; `Srui.Tests` is the harness's first
consumer.

# 5. What Is In The Packages

| Package | Contents | Native binaries |
|---|---|---|
| Srui | Widgets, dialogs, focus and navigation, shortcuts, the text engine, the SDL host, speech | prism.dll, SDL3.dll |
| Srui.Audio | Sounds, buses, effect chains, HRTF spatialisation, tweens | cosmos.dll, phonon.dll |
| Srui.Testing | The headless test harness: input simulation, utterance assertions, scenario record and replay | none |
| Srui.Templates | The `dotnet new` templates | none |

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
