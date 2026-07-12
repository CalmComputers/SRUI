namespace Srui.Audio;

/// <summary>
/// One effect in an ordered chain (<see cref="Sound.SetFxChain"/>,
/// <see cref="SoundGroup.SetFxChain"/>): an arbitrary ordered list,
/// kinds repeatable, each entry one node. Filters are miniaudio's
/// order-based family (order N = N*6 dB/oct); Binauralizer and
/// Positional are Steam Audio HRTFs and degrade to passthrough when
/// phonon is unavailable; the heavy kinds (reverb, delay, distortion,
/// vocoder, three-band EQ) are cosmos's DSP nodes — the same ones
/// behind <see cref="SoundGroup"/>'s classic fixed-slot bus effects,
/// which remain as the one-of-each convenience API.
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

    /// <summary>Convolution reverb. Null IrPath uses the built-in
    /// impulse response.</summary>
    public sealed record Reverb(
        double Wet, double Dry, double PredelayMs, double IrGain, double Width,
        double Decay, double LowcutHz, double HighcutHz, double Diffuse,
        string? IrPath = null) : SoundEffect;

    public sealed record Delay(double DelayMs, double Feedback, double Wet, double Dry)
        : SoundEffect;

    public sealed record Distortion(double Drive, double ToneHz, double Wet, double Dry)
        : SoundEffect;

    public sealed record Vocoder(
        int Bands, VocoderCarrier Carrier, double CarrierFreq,
        double AttackMs, double ReleaseMs, double Wet, double Dry,
        bool RandOn = false, double RandRate = 0, double RandDepth = 0,
        double Formant = 1, double Spread = 0, double Sibilance = 0) : SoundEffect;

    /// <summary>Three-band EQ (cosmos's ma_eq node).</summary>
    public sealed record Eq(
        double LowGainDb, double MidGainDb, double HighGainDb,
        double LowFreq, double MidFreq, double HighFreq, double MidQ) : SoundEffect;
}

/// <summary>Node lifecycle per effect kind — create, reinit in place,
/// destroy — shared by the per-sound and per-group chains.</summary>
internal static class FxNodes
{
    /// <summary>Creates and parameterizes a node; IntPtr.Zero when it
    /// could not be created (phonon unavailable) — the chain skips it.</summary>
    public static IntPtr Create(AudioEngine engine, SoundEffect spec)
    {
        var graph = engine.NodeGraph;
        var rate = engine.SampleRate;
        switch (spec)
        {
            case SoundEffect.Lowpass(var frequency, var order):
                return NativeMethods.cosmos_lpf_node_create(graph, rate, frequency, (uint)order);
            case SoundEffect.Highpass(var frequency, var order):
                return NativeMethods.cosmos_hpf_node_create(graph, rate, frequency, (uint)order);
            case SoundEffect.Bandpass(var frequency, var order):
                return NativeMethods.cosmos_bpf_node_create(graph, rate, frequency, (uint)order);
            case SoundEffect.LowShelf(var frequency, var gain, var slope):
                return NativeMethods.cosmos_loshelf_node_create(graph, rate, gain, slope, frequency);
            case SoundEffect.HighShelf(var frequency, var gain, var slope):
                return NativeMethods.cosmos_hishelf_node_create(graph, rate, gain, slope, frequency);
            case SoundEffect.Peaking(var frequency, var gain, var q):
                return NativeMethods.cosmos_peak_node_create(graph, rate, gain, q, frequency);
            case SoundEffect.Binauralizer:
            {
                var node = NativeMethods.cosmos_binauralizer_node_create(graph);
                if (node != IntPtr.Zero)
                    Reinit(engine, node, spec);
                return node;
            }
            case SoundEffect.Positional:
            {
                var node = NativeMethods.cosmos_binaural_node_create(graph, 2);
                if (node != IntPtr.Zero)
                    Reinit(engine, node, spec);
                return node;
            }
            case SoundEffect.Disperser:
            {
                var node = NativeMethods.cosmos_disperser_create(graph, rate);
                if (node != IntPtr.Zero)
                    Reinit(engine, node, spec);
                return node;
            }
            case SoundEffect.Reverb:
            {
                var node = NativeMethods.cosmos_reverb_create(graph, rate);
                if (node != IntPtr.Zero)
                    Reinit(engine, node, spec);
                return node;
            }
            case SoundEffect.Delay:
            {
                var node = NativeMethods.cosmos_delay_create(graph, rate);
                if (node != IntPtr.Zero)
                    Reinit(engine, node, spec);
                return node;
            }
            case SoundEffect.Distortion:
            {
                var node = NativeMethods.cosmos_distortion_create(graph, rate);
                if (node != IntPtr.Zero)
                    Reinit(engine, node, spec);
                return node;
            }
            case SoundEffect.Vocoder:
            {
                var node = NativeMethods.cosmos_vocoder_create(graph, rate);
                if (node != IntPtr.Zero)
                    Reinit(engine, node, spec);
                return node;
            }
            case SoundEffect.Eq:
            {
                var node = NativeMethods.cosmos_eq_create(graph, rate);
                if (node != IntPtr.Zero)
                    Reinit(engine, node, spec);
                return node;
            }
            default:
                return IntPtr.Zero;
        }
    }

    /// <summary>Swap parameters in place (glitch-free for the filter
    /// family; setter-based for the rest).</summary>
    public static void Reinit(AudioEngine engine, IntPtr node, SoundEffect spec)
    {
        var rate = engine.SampleRate;
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
            {
                var (lx, ly, lz) = SphericalToSteam(-width, 0);
                var (rx, ry, rz) = SphericalToSteam(width, 0);
                NativeMethods.ma_phonon_binauralizer_node_set_directions(
                    node, lx, ly, lz, rx, ry, rz);
                break;
            }
            case SoundEffect.Positional(var azimuth, var elevation, var distance):
            {
                var (x, y, z) = SphericalToSteam(azimuth, elevation);
                NativeMethods.ma_phonon_binaural_node_set_direction(node, x, y, z, (float)distance);
                break;
            }
            case SoundEffect.Disperser(var frequency, var count, var q):
                NativeMethods.ma_disperser_node_set_freq(node, (float)frequency);
                NativeMethods.ma_disperser_node_set_q(node, (float)q);
                NativeMethods.ma_disperser_node_set_stages(node, count);
                break;
            case SoundEffect.Reverb reverb:
                NativeMethods.ma_convreverb_node_set_wet(node, (float)reverb.Wet);
                NativeMethods.ma_convreverb_node_set_dry(node, (float)reverb.Dry);
                NativeMethods.ma_convreverb_node_set_predelay(node, (float)reverb.PredelayMs);
                NativeMethods.ma_convreverb_node_set_ir_gain(node, (float)reverb.IrGain);
                NativeMethods.ma_convreverb_node_set_width(node, (float)reverb.Width);
                NativeMethods.ma_convreverb_node_set_decay(node, (float)reverb.Decay);
                NativeMethods.ma_convreverb_node_set_lowcut(node, (float)reverb.LowcutHz);
                NativeMethods.ma_convreverb_node_set_highcut(node, (float)reverb.HighcutHz);
                NativeMethods.ma_convreverb_node_set_diffuse(node, (float)reverb.Diffuse);
                if (reverb.IrPath is { } irPath)
                    _ = NativeMethods.ma_convreverb_node_load_ir_file(node, irPath) == 0
                        || NativeMethods.ma_convreverb_node_load_default_ir(node) == 0;
                else
                    _ = NativeMethods.ma_convreverb_node_load_default_ir(node);
                break;
            case SoundEffect.Delay(var delayMs, var feedback, var wet, var dry):
                NativeMethods.ma_delay_fx_set_delay_ms(node, (float)delayMs);
                NativeMethods.ma_delay_fx_set_feedback(node, (float)feedback);
                NativeMethods.ma_delay_fx_set_wet(node, (float)wet);
                NativeMethods.ma_delay_fx_set_dry(node, (float)dry);
                break;
            case SoundEffect.Distortion(var drive, var tone, var wet, var dry):
                NativeMethods.ma_distortion_node_set_drive(node, (float)drive);
                NativeMethods.ma_distortion_node_set_tone(node, (float)tone);
                NativeMethods.ma_distortion_node_set_wet(node, (float)wet);
                NativeMethods.ma_distortion_node_set_dry(node, (float)dry);
                break;
            case SoundEffect.Vocoder vocoder:
                NativeMethods.ma_vocoder_node_set_bands(node, vocoder.Bands);
                NativeMethods.ma_vocoder_node_set_carrier(node, (int)vocoder.Carrier);
                NativeMethods.ma_vocoder_node_set_carrier_freq(node, (float)vocoder.CarrierFreq);
                NativeMethods.ma_vocoder_node_set_attack(node, (float)vocoder.AttackMs);
                NativeMethods.ma_vocoder_node_set_release(node, (float)vocoder.ReleaseMs);
                NativeMethods.ma_vocoder_node_set_wet(node, (float)vocoder.Wet);
                NativeMethods.ma_vocoder_node_set_dry(node, (float)vocoder.Dry);
                NativeMethods.ma_vocoder_node_set_rand(node, vocoder.RandOn ? 1 : 0);
                NativeMethods.ma_vocoder_node_set_rand_rate(node, (float)vocoder.RandRate);
                NativeMethods.ma_vocoder_node_set_rand_depth(node, (float)vocoder.RandDepth);
                NativeMethods.ma_vocoder_node_set_formant(node, (float)vocoder.Formant);
                NativeMethods.ma_vocoder_node_set_spread(node, (float)vocoder.Spread);
                NativeMethods.ma_vocoder_node_set_sibilance(node, (float)vocoder.Sibilance);
                break;
            case SoundEffect.Eq eq:
                NativeMethods.ma_eq_node_set_low_gain(node, (float)eq.LowGainDb);
                NativeMethods.ma_eq_node_set_mid_gain(node, (float)eq.MidGainDb);
                NativeMethods.ma_eq_node_set_high_gain(node, (float)eq.HighGainDb);
                NativeMethods.ma_eq_node_set_low_freq(node, (float)eq.LowFreq);
                NativeMethods.ma_eq_node_set_mid_freq(node, (float)eq.MidFreq);
                NativeMethods.ma_eq_node_set_high_freq(node, (float)eq.HighFreq);
                NativeMethods.ma_eq_node_set_mid_q(node, (float)eq.MidQ);
                break;
        }
    }

    public static void Destroy(IntPtr node, SoundEffect spec)
    {
        switch (spec)
        {
            case SoundEffect.Lowpass:
                NativeMethods.cosmos_lpf_node_destroy(node);
                break;
            case SoundEffect.Highpass:
                NativeMethods.cosmos_hpf_node_destroy(node);
                break;
            case SoundEffect.Bandpass:
                NativeMethods.cosmos_bpf_node_destroy(node);
                break;
            case SoundEffect.LowShelf:
                NativeMethods.cosmos_loshelf_node_destroy(node);
                break;
            case SoundEffect.HighShelf:
                NativeMethods.cosmos_hishelf_node_destroy(node);
                break;
            case SoundEffect.Peaking:
                NativeMethods.cosmos_peak_node_destroy(node);
                break;
            case SoundEffect.Binauralizer:
                NativeMethods.cosmos_binauralizer_node_destroy(node);
                break;
            case SoundEffect.Positional:
                NativeMethods.cosmos_binaural_node_destroy(node);
                break;
            case SoundEffect.Disperser:
                NativeMethods.cosmos_disperser_destroy(node);
                break;
            case SoundEffect.Reverb:
                NativeMethods.cosmos_reverb_destroy(node);
                break;
            case SoundEffect.Delay:
                NativeMethods.cosmos_delay_destroy(node);
                break;
            case SoundEffect.Distortion:
                NativeMethods.cosmos_distortion_destroy(node);
                break;
            case SoundEffect.Vocoder:
                NativeMethods.cosmos_vocoder_destroy(node);
                break;
            case SoundEffect.Eq:
                NativeMethods.cosmos_eq_destroy(node);
                break;
        }
    }

    /// <summary>Same shape for in-place reinit: same kind, and (because
    /// its delay lines resize) the same Disperser stage count.</summary>
    public static bool SameShape(SoundEffect a, SoundEffect b)
    {
        if (a.GetType() != b.GetType())
            return false;
        if (a is SoundEffect.Disperser(_, var countA, _)
            && b is SoundEffect.Disperser(_, var countB, _))
            return countA == countB;
        return true;
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
}

/// <summary>
/// The live node chain behind <see cref="Sound.SetFxChain"/>: wires
/// source → effects (in order) → downstream. Parameter-only changes
/// reinit nodes in place; a change in kind or order tears down and
/// rebuilds. Phonon effects that fail to create are skipped — the
/// chain audibly degrades rather than failing.
/// </summary>
internal sealed class FxChain : IDisposable
{
    private readonly AudioEngine _engine;
    private readonly IntPtr _source;
    private readonly IntPtr _downstream;
    private readonly List<Entry> _entries = new();

    private readonly record struct Entry(SoundEffect Spec, IntPtr Node);

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
                    FxNodes.Reinit(_engine, _entries[i].Node, effects[i]);
                    _entries[i] = _entries[i] with { Spec = effects[i] };
                }
            }
            return;
        }
        DetachAll();
        DestroyAll();
        foreach (var spec in effects)
        {
            var node = FxNodes.Create(_engine, spec);
            if (node != IntPtr.Zero)
                _entries.Add(new Entry(spec, node));
        }
        Wire();
    }

    private bool CanReinitInPlace(IReadOnlyList<SoundEffect> effects)
    {
        if (effects.Count != _entries.Count)
            return false;
        for (var i = 0; i < effects.Count; i++)
            if (!FxNodes.SameShape(effects[i], _entries[i].Spec))
                return false;
        return true;
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
            FxNodes.Destroy(entry.Node, entry.Spec);
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
