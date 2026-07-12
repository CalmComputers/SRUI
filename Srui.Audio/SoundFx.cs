namespace Srui.Audio;

/// <summary>
/// One effect in a per-sound chain (<see cref="Sound.SetFxChain"/>) —
/// the media-playback counterpart of <see cref="SoundGroup"/>'s
/// fixed-slot bus effects: an arbitrary ordered list, kinds repeatable,
/// each entry one node. Filters are miniaudio's order-based family
/// (order N = N*6 dB/oct); Binauralizer and Positional are Steam Audio
/// HRTFs and degrade to passthrough when phonon is unavailable.
/// </summary>
public abstract record SoundEffect
{
    public sealed record Lowpass(double Frequency, int Order) : SoundEffect;

    public sealed record Highpass(double Frequency, int Order) : SoundEffect;

    public sealed record Bandpass(double Frequency, int Order) : SoundEffect;

    public sealed record LowShelf(double Frequency, double GainDb, double Slope) : SoundEffect;

    public sealed record HighShelf(double Frequency, double GainDb, double Slope) : SoundEffect;

    public sealed record Peaking(double Frequency, double GainDb, double Q) : SoundEffect;

    /// <summary>Stereo HRTF widener: L and R spatialised at ∓width°.</summary>
    public sealed record Binauralizer(double WidthDeg) : SoundEffect;

    /// <summary>3D HRTF placement at a spherical position.</summary>
    public sealed record Positional(double AzimuthDeg, double ElevationDeg, double DistanceM)
        : SoundEffect;

    public sealed record Disperser(double Frequency, int Count, double Q) : SoundEffect;
}

/// <summary>
/// The live node chain behind <see cref="Sound.SetFxChain"/>: wires
/// source → effects (in order) → downstream. Parameter-only changes
/// reinit nodes in place (glitch-free); a change in kind or order tears
/// down and rebuilds. Phonon effects that fail to create are skipped —
/// the chain audibly degrades rather than failing.
/// </summary>
internal sealed class FxChain : IDisposable
{
    private readonly AudioEngine _engine;
    private readonly IntPtr _source;
    private readonly IntPtr _downstream;
    private readonly List<Entry> _entries = new();

    private readonly record struct Entry(SoundEffect Spec, IntPtr Node, bool Phonon);

    public FxChain(AudioEngine engine, IntPtr sourceNode, IntPtr downstream)
    {
        _engine = engine;
        _source = sourceNode;
        _downstream = downstream;
    }

    public void Apply(IReadOnlyList<SoundEffect> effects)
    {
        if (CanReinitInPlace(effects))
        {
            for (var i = 0; i < effects.Count; i++)
            {
                if (!effects[i].Equals(_entries[i].Spec))
                {
                    Reinit(_entries[i].Node, effects[i]);
                    _entries[i] = _entries[i] with { Spec = effects[i] };
                }
            }
            return;
        }
        Rebuild(effects);
    }

    /// <summary>Same shape — same count, kinds pairwise, and (because
    /// its delay lines resize) the same Disperser stage count.</summary>
    private bool CanReinitInPlace(IReadOnlyList<SoundEffect> effects)
    {
        if (effects.Count != _entries.Count)
            return false;
        for (var i = 0; i < effects.Count; i++)
        {
            var fresh = effects[i];
            var live = _entries[i].Spec;
            if (fresh.GetType() != live.GetType())
                return false;
            if (fresh is SoundEffect.Disperser(_, var count, _)
                && live is SoundEffect.Disperser(_, var liveCount, _)
                && count != liveCount)
                return false;
        }
        return true;
    }

    private void Rebuild(IReadOnlyList<SoundEffect> effects)
    {
        DetachAll();
        DestroyAll();
        foreach (var spec in effects)
        {
            var (node, phonon) = Create(spec);
            if (node != IntPtr.Zero)
                _entries.Add(new Entry(spec, node, phonon));
        }
        Wire();
    }

    private (IntPtr Node, bool Phonon) Create(SoundEffect spec)
    {
        var graph = _engine.NodeGraph;
        var rate = _engine.SampleRate;
        switch (spec)
        {
            case SoundEffect.Lowpass(var frequency, var order):
                return (NativeMethods.cosmos_lpf_node_create(graph, rate, frequency, (uint)order), false);
            case SoundEffect.Highpass(var frequency, var order):
                return (NativeMethods.cosmos_hpf_node_create(graph, rate, frequency, (uint)order), false);
            case SoundEffect.Bandpass(var frequency, var order):
                return (NativeMethods.cosmos_bpf_node_create(graph, rate, frequency, (uint)order), false);
            case SoundEffect.LowShelf(var frequency, var gain, var slope):
                return (NativeMethods.cosmos_loshelf_node_create(graph, rate, gain, slope, frequency), false);
            case SoundEffect.HighShelf(var frequency, var gain, var slope):
                return (NativeMethods.cosmos_hishelf_node_create(graph, rate, gain, slope, frequency), false);
            case SoundEffect.Peaking(var frequency, var gain, var q):
                return (NativeMethods.cosmos_peak_node_create(graph, rate, gain, q, frequency), false);
            case SoundEffect.Binauralizer(var width):
            {
                var node = NativeMethods.cosmos_binauralizer_node_create(graph);
                if (node != IntPtr.Zero)
                    SetBinauralizerWidth(node, width);
                return (node, true);
            }
            case SoundEffect.Positional(var azimuth, var elevation, var distance):
            {
                var node = NativeMethods.cosmos_binaural_node_create(graph, 2);
                if (node != IntPtr.Zero)
                    SetPosition(node, azimuth, elevation, distance);
                return (node, true);
            }
            case SoundEffect.Disperser(var frequency, var count, var q):
            {
                var node = NativeMethods.cosmos_disperser_create(graph, rate);
                if (node != IntPtr.Zero)
                {
                    NativeMethods.ma_disperser_node_set_freq(node, (float)frequency);
                    NativeMethods.ma_disperser_node_set_q(node, (float)q);
                    NativeMethods.ma_disperser_node_set_stages(node, count);
                }
                return (node, false);
            }
            default:
                return (IntPtr.Zero, false);
        }
    }

    private void Reinit(IntPtr node, SoundEffect spec)
    {
        var rate = _engine.SampleRate;
        switch (spec)
        {
            case SoundEffect.Lowpass(var frequency, var order):
                NativeMethods.cosmos_lpf_node_reinit(node, rate, frequency, (uint)order);
                break;
            case SoundEffect.Highpass(var frequency, var order):
                NativeMethods.cosmos_hpf_node_reinit(node, rate, frequency, (uint)order);
                break;
            case SoundEffect.Bandpass(var frequency, var order):
                NativeMethods.cosmos_bpf_node_reinit(node, rate, frequency, (uint)order);
                break;
            case SoundEffect.LowShelf(var frequency, var gain, var slope):
                NativeMethods.cosmos_loshelf_node_reinit(node, rate, gain, slope, frequency);
                break;
            case SoundEffect.HighShelf(var frequency, var gain, var slope):
                NativeMethods.cosmos_hishelf_node_reinit(node, rate, gain, slope, frequency);
                break;
            case SoundEffect.Peaking(var frequency, var gain, var q):
                NativeMethods.cosmos_peak_node_reinit(node, rate, gain, q, frequency);
                break;
            case SoundEffect.Binauralizer(var width):
                SetBinauralizerWidth(node, width);
                break;
            case SoundEffect.Positional(var azimuth, var elevation, var distance):
                SetPosition(node, azimuth, elevation, distance);
                break;
            case SoundEffect.Disperser(var frequency, _, var q):
                NativeMethods.ma_disperser_node_set_freq(node, (float)frequency);
                NativeMethods.ma_disperser_node_set_q(node, (float)q);
                break;
        }
    }

    private static void SetBinauralizerWidth(IntPtr node, double widthDeg)
    {
        var (lx, ly, lz) = SphericalToSteam(-widthDeg, 0);
        var (rx, ry, rz) = SphericalToSteam(widthDeg, 0);
        NativeMethods.ma_phonon_binauralizer_node_set_directions(node, lx, ly, lz, rx, ry, rz);
    }

    private static void SetPosition(IntPtr node, double azimuthDeg, double elevationDeg, double distanceM)
    {
        var (x, y, z) = SphericalToSteam(azimuthDeg, elevationDeg);
        NativeMethods.ma_phonon_binaural_node_set_direction(node, x, y, z, (float)distanceM);
    }

    /// <summary>Steam Audio coordinates: +X right, +Y up, -Z ahead.
    /// Positive azimuth rotates rightward; positive elevation rises.</summary>
    internal static (float X, float Y, float Z) SphericalToSteam(
        double azimuthDeg, double elevationDeg)
    {
        var azimuth = azimuthDeg * Math.PI / 180.0;
        var elevation = elevationDeg * Math.PI / 180.0;
        var cosElevation = Math.Cos(elevation);
        return (
            (float)(Math.Sin(azimuth) * cosElevation),
            (float)Math.Sin(elevation),
            (float)(-(Math.Cos(azimuth) * cosElevation)));
    }

    private void Wire()
    {
        NativeMethods.ma_node_detach_output_bus(_source, 0);
        var previous = _source;
        foreach (var entry in _entries)
        {
            NativeMethods.ma_node_attach_output_bus(previous, 0, entry.Node, 0);
            previous = entry.Node;
        }
        NativeMethods.ma_node_attach_output_bus(previous, 0, _downstream, 0);
    }

    private void DetachAll()
    {
        NativeMethods.ma_node_detach_output_bus(_source, 0);
        foreach (var entry in _entries)
            NativeMethods.ma_node_detach_output_bus(entry.Node, 0);
    }

    private void DestroyAll()
    {
        foreach (var entry in _entries)
        {
            switch (entry.Spec)
            {
                case SoundEffect.Lowpass:
                    NativeMethods.cosmos_lpf_node_destroy(entry.Node);
                    break;
                case SoundEffect.Highpass:
                    NativeMethods.cosmos_hpf_node_destroy(entry.Node);
                    break;
                case SoundEffect.Bandpass:
                    NativeMethods.cosmos_bpf_node_destroy(entry.Node);
                    break;
                case SoundEffect.LowShelf:
                    NativeMethods.cosmos_loshelf_node_destroy(entry.Node);
                    break;
                case SoundEffect.HighShelf:
                    NativeMethods.cosmos_hishelf_node_destroy(entry.Node);
                    break;
                case SoundEffect.Peaking:
                    NativeMethods.cosmos_peak_node_destroy(entry.Node);
                    break;
                case SoundEffect.Binauralizer:
                    NativeMethods.cosmos_binauralizer_node_destroy(entry.Node);
                    break;
                case SoundEffect.Positional:
                    NativeMethods.cosmos_binaural_node_destroy(entry.Node);
                    break;
                case SoundEffect.Disperser:
                    NativeMethods.cosmos_disperser_destroy(entry.Node);
                    break;
            }
        }
        _entries.Clear();
    }

    /// <summary>Tear down the chain and route the source straight to
    /// its downstream again.</summary>
    public void Dispose()
    {
        DetachAll();
        DestroyAll();
        NativeMethods.ma_node_attach_output_bus(_source, 0, _downstream, 0);
    }
}
