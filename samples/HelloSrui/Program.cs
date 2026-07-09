// HelloSrui: the smallest useful srui app, built against a compiled
// drop (see README.md) rather than the source tree.
//
// Tab moves between widgets. The Position slider slides a ping across
// the stereo field (HRTF when Steam Audio is available); the Reverb
// checkbox puts a room on the effect bus; Enter anywhere (or Ctrl+G)
// greets; Escape quits.

using Srui;
using Srui.Audio;

using var app = new SruiApp("Hello SRUI");
Console.WriteLine($"speech backend: {app.Voice?.BackendName}");

// ── Audio: one manager, one effect bus, one positioned sound ──

// The app owns the manager and drives its automation (pitch tweens,
// spatialization) from the event loop — no ticking to wire. Only
// UI-less consumers of Srui.Audio call SoundManager.Tick themselves.
var audio = app.Audio;
// Cosmos convention: angle 0 faces +X (east); face +Y so positions
// along X read as left/right.
audio.SetListener(0.0f, 0.0f, 0.0f, 90.0f);
using var bus = audio.CreateGroup();

var pingWav = Path.Combine(Path.GetTempPath(), "hello-srui-ping.wav");
WritePingWav(pingWav);
using var ping = audio.CreateSound(bus);
ping.Load(pingWav);
ping.Hrtf = audio.IsHrtfAvailable;
ping.SetPosition(0.0f, 2.0f, 0.0f);

// ── UI ──

new Label(app, "Hello SRUI");
var name = new EditBox(app, "Your name");
var position = new Slider(app, "Position", 0, -4, 4);
var reverb = new CheckBox(app, "Reverb");
var greet = new Button(app, "Greet");
var quit = new Button(app, "Quit");

// Enter anywhere presses Greet; Escape anywhere presses Quit. Ctrl+G
// is a widget shortcut: it presses Greet without moving focus, and is
// spoken with the button ("Greet button control g").
app.SetPrimary(greet);
app.SetCancel(quit);
greet.AddShortcut(KeyCombo.WithCtrl(Key.Char('g')), ShortcutAction.Activate);

// Arrowing the slider slides the ping across a lane two units ahead.
position.Changed += () =>
{
    ping.SetPosition(position.Value, 2.0f, 0.0f);
    ping.Stop();
    ping.Play();
};

reverb.Toggled += on =>
{
    if (on)
        bus.EnableReverb(
            wet: 0.5f, dry: 0.9f, predelayMs: 20.0f, irGain: 1.0f, width: 1.0f,
            decay: 0.6f, lowcutHz: 120.0f, highcutHz: 9000.0f, diffuse: 0.5f);
    else
        bus.DisableReverb();
};

greet.Activated += () =>
{
    var who = string.IsNullOrEmpty(name.Text) ? "stranger" : name.Text;
    app.Announce($"Hello, {who}.");
};

quit.Activated += app.Quit;

app.Run();
return;

// A 300ms 880Hz ping with exponential decay, 16-bit mono WAV — so the
// sample carries no asset files.
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
