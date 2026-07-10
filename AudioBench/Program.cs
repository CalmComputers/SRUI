// Audio stress benchmark: many looping sources at many 3D positions,
// all sounding at once. Measures the load phase (decode cache across
// repeated files), HRTF convolver pooling (sources sharing a position
// share a convolver), steady-state Tick cost, managed allocations
// during the run (the rule says zero), and whole-process CPU — which
// includes the device-thread mixing that Tick itself never does.
//
//   dotnet run --project AudioBench [-c Release]
//       [-- --sources 500 --positions 50 --seconds 10]
//
// Assets are the OGG files in AudioBench/assets/ (gitignored — bring
// your own); sources cycle through them.

using System.Diagnostics;
using Srui.Audio;

var sources = ArgInt("--sources", 500);
var positions = ArgInt("--positions", 50);
var seconds = ArgInt("--seconds", 10);

var assetDir = Path.Combine(AppContext.BaseDirectory, "assets");
var files = Directory.Exists(assetDir)
    ? Directory.GetFiles(assetDir, "*.ogg")
    : Array.Empty<string>();
if (files.Length == 0)
{
    Console.WriteLine($"no .ogg files in {assetDir} — place some in AudioBench/assets/");
    return 1;
}
Array.Sort(files, StringComparer.OrdinalIgnoreCase);

using var manager = new SoundManager();
manager.SetListener(0.0f, 0.0f, 0.0f, 90.0f);
Console.WriteLine(
    $"device: {manager.SampleRate} Hz, period {manager.DevicePeriodFrames} frames "
    + $"(~{manager.DevicePeriodFrames * 1000.0 / manager.SampleRate:F2}ms), "
    + $"hrtf {(manager.IsHrtfAvailable ? "available" : "unavailable")}");
Console.WriteLine($"{sources} sources over {files.Length} files at {positions} positions, {seconds}s run");

// Positions: a Fibonacci-lattice sphere of radius 10 around the
// listener, so the load spreads through the full 3D field.
var pos = new (float X, float Y, float Z)[positions];
for (var i = 0; i < positions; i++)
{
    var y = 1.0f - 2.0f * (i + 0.5f) / positions;
    var r = MathF.Sqrt(1.0f - y * y);
    var phi = i * 2.3999632f; // golden angle
    pos[i] = (10.0f * r * MathF.Cos(phi), 10.0f * y, 10.0f * r * MathF.Sin(phi));
}

// ── Load ──

var sw = Stopwatch.StartNew();
var sounds = new Sound[sources];
// Uncorrelated sources sum in RMS as sqrt(N); scale each down by that
// so the mix does not clip. The assumption only holds if the copies of
// one file are NOT sample-aligned — started together and looping in
// lockstep they sum coherently (17 copies = 17x amplitude, not 4.1x) —
// so every source starts at a random offset into its file.
var volume = 1.0f / MathF.Sqrt(sources);
var rng = new Random(12345);
for (var i = 0; i < sources; i++)
{
    var s = manager.CreateSound();
    s.Load(files[i % files.Length]);
    s.Hrtf = manager.IsHrtfAvailable;
    var (x, y, z) = pos[i % positions];
    s.SetPosition(x, y, z);
    s.Looping = true;
    s.BaseVolume = volume;
    s.PlaybackPosition = (ulong)(rng.NextDouble() * s.Length);
    sounds[i] = s;
}
Console.WriteLine($"load: {sw.ElapsedMilliseconds}ms total, "
    + $"{sw.Elapsed.TotalMilliseconds / sources:F2}ms per source");

// ── Start ──

sw.Restart();
foreach (var s in sounds)
    s.Play();
Console.WriteLine($"start: {sw.ElapsedMilliseconds}ms; "
    + $"hrtf convolvers {manager.ActiveHrtfConvolvers} (positions: {positions})");

// ── Steady state ──

var tickUs = new List<double>(seconds * 400);
var proc = Process.GetCurrentProcess();
var gen0 = GC.CollectionCount(0);
var gen1 = GC.CollectionCount(1);
var gen2 = GC.CollectionCount(2);
var cpuBefore = proc.TotalProcessorTime;
var allocBefore = GC.GetTotalAllocatedBytes(precise: true);
sw.Restart();
while (sw.Elapsed.TotalSeconds < seconds)
{
    var t0 = Stopwatch.GetTimestamp();
    manager.Tick();
    tickUs.Add((Stopwatch.GetTimestamp() - t0) * 1_000_000.0 / Stopwatch.Frequency);
    Thread.Sleep(5);
}
var wall = sw.Elapsed;
var allocAfter = GC.GetTotalAllocatedBytes(precise: true);
proc.Refresh();
var cpu = proc.TotalProcessorTime - cpuBefore;

tickUs.Sort();
Console.WriteLine($"ticks: {tickUs.Count} over {wall.TotalSeconds:F1}s; "
    + $"median {tickUs[tickUs.Count / 2]:F1}us, "
    + $"p99 {tickUs[(int)(tickUs.Count * 0.99)]:F1}us, "
    + $"max {tickUs[^1]:F1}us");
Console.WriteLine($"allocations during run: {allocAfter - allocBefore} bytes; "
    + $"collections gen0 {GC.CollectionCount(0) - gen0}, "
    + $"gen1 {GC.CollectionCount(1) - gen1}, gen2 {GC.CollectionCount(2) - gen2}");
Console.WriteLine($"process cpu: {cpu.TotalMilliseconds:F0}ms over {wall.TotalMilliseconds:F0}ms wall "
    + $"= {cpu.TotalMilliseconds / wall.TotalMilliseconds:F2} cores "
    + $"({cpu.TotalMilliseconds / wall.TotalMilliseconds / Environment.ProcessorCount * 100.0:F1}% of {Environment.ProcessorCount})");

// ── Teardown ──

sw.Restart();
foreach (var s in sounds)
    s.Dispose();
Console.WriteLine($"dispose: {sw.ElapsedMilliseconds}ms; "
    + $"hrtf convolvers now {manager.ActiveHrtfConvolvers}");
return 0;

int ArgInt(string name, int fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v)
        ? v
        : fallback;
}
