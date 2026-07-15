// Srui.Audio end-to-end example. Synthesizes a short ping (no asset
// needed), then: circles it around the listener with HRTF, replays it
// time-stretched, and runs it through a delay bus.

using Srui.Audio;

var wav = Path.Combine(Path.GetTempPath(), "srui-audio-ping.wav");
WritePingWav(wav);

using var manager = new SoundManager();
// Face +Y (angle 90); the cosmos default of 0 faces +X (east).
manager.SetListener(0.0f, 0.0f, 0.0f, 90.0f);
Console.WriteLine($"sample rate: {manager.SampleRate}, HRTF: {manager.IsHrtfAvailable}");

// 1. HRTF circle: the ping orbits the listener. Positioning is
// entity-level: the entity owns the place in space, the sound just
// plays into the entity's group.
using (var orbiter = manager.CreateEntity())
using (var sound = manager.CreateSound(orbiter.Group))
{
    orbiter.Hrtf = manager.IsHrtfAvailable;
    sound.Load(wav);
    sound.Looping = true;
    sound.Play();
    Console.WriteLine("circling with HRTF...");
    for (var step = 0; step < 240; step++)
    {
        var angle = step * (MathF.PI * 2.0f / 240.0f);
        orbiter.SetPosition(5.0f * MathF.Cos(angle), 5.0f * MathF.Sin(angle), 0.0f);
        manager.Tick();
        Thread.Sleep(25);
    }
    sound.Stop();
}

// 2. Time stretch: same ping, twice as long, pitch preserved.
using (var sound = manager.CreateSound())
{
    Console.WriteLine("stretched 2x...");
    sound.LoadStretched(wav, 2.0f);
    sound.Play();
    while (sound.IsPlaying && !sound.AtEnd)
        Thread.Sleep(25);
}

// 3. A delay bus: ping through echo.
using (var echoBus = manager.CreateGroup())
using (var sound = manager.CreateSound(echoBus))
{
    Console.WriteLine("through a delay bus...");
    echoBus.SetFxChain(new SoundEffect[]
    {
        new SoundEffect.Delay(DelayMs: 250, Feedback: 0.45, Wet: 0.5, Dry: 0.8),
    });
    sound.Load(wav);
    sound.Play();
    Thread.Sleep(2500);
}

// 4. One convolution per entity: however many sounds an entity plays,
// its group mixes them into a single HRTF convolver.
if (manager.IsHrtfAvailable)
{
    Console.WriteLine("entity convolution...");
    using var bell = manager.CreateEntity();
    bell.Hrtf = true;
    bell.SetPosition(3.0f, 3.0f, 0.0f);
    var chord = new Sound[4];
    for (var i = 0; i < chord.Length; i++)
    {
        chord[i] = manager.CreateSound(bell.Group);
        chord[i].Load(wav);
        chord[i].Pitch = 1.0f + i * 0.25f;
    }
    Console.WriteLine($"  4 sounds, one entity: {manager.ActiveHrtfConvolvers} convolver(s)");
    foreach (var s in chord)
    {
        s.Play();
        Thread.Sleep(120);
    }
    Thread.Sleep(500);
    foreach (var s in chord)
        s.Dispose();
}

// 5. Oneshots: fire and forget — the manager owns the sound and reaps
// it on the first Tick after it finishes; no reference to keep.
Console.WriteLine("fire-and-forget oneshots...");
using (var thrower = manager.CreateEntity())
{
    for (var i = 0; i < 3; i++)
    {
        thrower.SetPosition(i * 2.0f - 2.0f, 3.0f, 0.0f);
        var shot = manager.CreateSound(thrower.Group, oneshot: true);
        shot.Load(wav);
        shot.Play();
        for (var t = 0; t < 20; t++)
        {
            manager.Tick();
            Thread.Sleep(25);
        }
    }
}

Console.WriteLine("done");
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
