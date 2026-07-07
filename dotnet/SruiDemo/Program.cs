// SRUI end-to-end demo, C# edition: SDL window in, Prism speech and
// Srui.Audio sound out.
//
// Tab / Shift+Tab move focus; Alt+arrows walk the hierarchy; arrows and
// typeahead work in the list; Space toggles the checkbox. Greet is the
// primary widget: Enter anywhere presses it (Ctrl+G too, as a host-side
// binding), and it reads the live name, list, and checkbox state.
// Audio: the Fruits list plays an HRTF ping positioned by selection, the
// Volume slider drives the SFX bus, and the Views tabs switch the bus
// effect (Library dry, Playlist delay, Effects reverb).
// Escape (or Enter on Quit, or closing the window) exits.

using Srui;
using Srui.Audio;

using var app = new SruiApp("SRUI Demo (C#)");
Console.WriteLine($"speech backend: {app.Voice.BackendName}");

using var audio = new SoundManager();
// Cosmos convention: angle 0 faces +X (east). Face +Y (north/forward)
// so fruit positions along X read as left/right.
audio.SetListener(0.0f, 0.0f, 0.0f, 90.0f);
using var sfxBus = audio.CreateGroup();
var pingWav = Path.Combine(Path.GetTempPath(), "srui-demo-ping.wav");
WritePingWav(pingWav);
using var ping = audio.CreateSound(sfxBus);
ping.Load(pingWav);
ping.Hrtf = audio.IsHrtfAvailable;
ping.SetPosition(0.0f, 2.0f, 0.0f);

new Label(app, "SRUI demo, C sharp edition");
var name = new EditBox(app, "Your name");
new EditBox(app, "Notes", multiline: true);
var greet = new Button(app, "Greet");
var wrap = new CheckBox(app, "Word Wrap");
var options = new Group(app, "Options");
new CheckBox(options, "Autosave", isChecked: true);
new CheckBox(options, "Telemetry");
var fruits = new ListBox(
    app, "Fruits",
    ["apple", "banana", "cherry", "date", "elderberry"],
    numbered: true);
var volume = new Slider(app, "Volume", 50, 0, 100, unit: "%");
var views = new TabControl(app, "Views", ["Library", "Playlist", "Effects"]);
new FilterListBox(
    app, "Commands",
    [
        "Save File", "Save As", "Open File", "Open Recent", "Close Tab",
        "Find", "Find Next", "Replace", "Go To Line", "Toggle Word Wrap",
        "Zoom In", "Zoom Out",
    ]);
new ShortcutField(app, "Custom shortcut");
var quit = new Button(app, "Quit");

// Enter anywhere presses Greet; Escape anywhere presses Quit.
app.SetPrimary(greet);
app.SetCancel(quit);

greet.Activated += Greet;
// Shift+Enter on Greet opens a reviewable status dialog.
greet.SecondaryActivated += () => app.ShowStatus(
    "About",
    "SRUI demo, C sharp edition.\nA screen-reader-first UI toolkit.\n"
        + "This text lives in a read-only edit box: arrows, words, and\n"
        + "selection all work. Escape or Close returns to the demo.");
quit.Activated += () => app.Confirm("Really quit?", onYes: app.Quit);

// Host-side bindings: Ctrl+G greets from anywhere.
app.UnhandledInput = input =>
{
    if (input.IsRawKey(Keys.Char('g'), Mods.Ctrl))
    {
        Greet();
        return true;
    }
    return false;
};

// ── Audio wiring ──

// Selecting a fruit plays the ping from that direction (index → x).
sfxBus.Volume = volume.Value / 100.0f;
fruits.Changed += () =>
{
    var index = fruits.SelectedIndex;
    ping.SetPosition((index - 2) * 2.0f, 2.0f, 0.0f);
    ping.Stop();
    ping.Play();
};

// The Volume slider drives the SFX bus.
volume.Changed += () => sfxBus.Volume = volume.Value / 100.0f;

// The Views tabs switch the bus effect, with an audible confirmation.
views.Changed += () =>
{
    switch (views.ActiveIndex)
    {
        case 0: // Library: dry
            sfxBus.DisableDelay();
            sfxBus.DisableReverb();
            break;
        case 1: // Playlist: echo
            sfxBus.DisableReverb();
            sfxBus.EnableDelay(delayMs: 220.0f, feedback: 0.4f, wet: 0.5f, dry: 0.9f);
            break;
        case 2: // Effects: reverb
            sfxBus.DisableDelay();
            sfxBus.EnableReverb(
                wet: 0.5f, dry: 0.9f, predelayMs: 20.0f, irGain: 1.0f, width: 1.0f,
                decay: 0.6f, lowcutHz: 120.0f, highcutHz: 9000.0f, diffuse: 0.5f);
            break;
    }
    ping.Stop();
    ping.Play();
};

// Audio automation rides the ticker system.
var audioTicker = app.StartTicker(50);
audioTicker.Tick += audio.Tick;

// A ticker: once a minute, note the uptime.
var minutes = 0;
var ticker = app.StartTicker(60_000);
ticker.Tick += () => app.Announce($"Demo running for {++minutes} minute{(minutes == 1 ? "" : "s")}.");

app.Run();
return;

// A 300ms 880Hz ping with exponential decay, 16-bit mono WAV.
static void WritePingWav(string path)
{
    const int sampleRate = 44100;
    const int frames = sampleRate * 3 / 10;
    using var writer = new BinaryWriter(File.Create(path));
    writer.Write("RIFF"u8);
    writer.Write(36 + frames * 2);
    writer.Write("WAVE"u8);
    writer.Write("fmt "u8);
    writer.Write(16);
    writer.Write((short)1); // PCM
    writer.Write((short)1); // mono
    writer.Write(sampleRate);
    writer.Write(sampleRate * 2);
    writer.Write((short)2);
    writer.Write((short)16);
    writer.Write("data"u8);
    writer.Write(frames * 2);
    for (var i = 0; i < frames; i++)
    {
        var t = i / (float)sampleRate;
        var sample = MathF.Sin(2.0f * MathF.PI * 880.0f * t) * MathF.Exp(-8.0f * t) * 0.5f;
        writer.Write((short)(sample * short.MaxValue));
    }
}

// Compose and queue the greeting from live widget state.
void Greet()
{
    var who = string.IsNullOrEmpty(name.Text) ? "stranger" : name.Text;
    var fruit = fruits.SelectedItem ?? "nothing";
    app.Announce(
        $"Hello, {who}. The fruit is {fruit}, and word wrap is {(wrap.Checked ? "on" : "off")}.");
}
