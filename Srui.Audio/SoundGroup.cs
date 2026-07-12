namespace Srui.Audio;

/// <summary>Multimode filter types for <see cref="SoundGroup.EnableFilter"/>.</summary>
public enum FilterMode
{
    Lowpass = 0,
    Highpass = 1,
    Bandpass = 2,
    Notch = 3,
    Peak = 4,
    LowShelf = 5,
    HighShelf = 6,
    Allpass = 7,
}

/// <summary>Vocoder carrier waveforms.</summary>
public enum VocoderCarrier
{
    Saw = 0,
    Pulse = 1,
    Noise = 2,
    SuperSaw = 3,
}

/// <summary>
/// A bus: sounds (and nested groups) attach to it, and it applies volume,
/// pitch, and an optional effect chain to everything routed through it.
/// When effects are enabled the audio flows
/// group → filter → EQ → distortion → vocoder → disperser → delay →
/// reverb → downstream, skipping disabled slots.
/// Create via <see cref="SoundManager.CreateGroup"/>.
/// </summary>
public sealed class SoundGroup : IDisposable
{
    private readonly SoundManager _manager;
    private readonly SoundGroup? _parent;
    private IntPtr _group;

    private float _baseVolume = 1.0f;
    private float _basePitch = 1.0f;
    private PitchTween _pitchTween;
    private bool _tweening;

    // Effect chain slots, in wiring order.
    private IntPtr _filter;
    private IntPtr _eq;
    private IntPtr _distortion;
    private IntPtr _vocoder;
    private IntPtr _disperser;
    private IntPtr _delay;
    private IntPtr _reverb;
    private float _reverbDecay = float.NaN;

    // Last-applied effect parameters, captured by the Set methods so an
    // engine rebuild (SoundManager.Reconfigure) can replay the chain.
    private FilterState? _filterState;
    private EqState? _eqState;
    private DistortionState? _distortionState;
    private VocoderState? _vocoderState;
    private DisperserState? _disperserState;
    private DelayState? _delayState;
    private ReverbState? _reverbState;
    private string? _reverbIrPath;

    // Native-only state snapshotted by ReleaseNative for RecreateNative.
    private bool _rebuildStopped;

    public bool IsDisposed { get; private set; }

    internal SoundGroup(SoundManager manager, SoundGroup? parent)
    {
        _manager = manager;
        _parent = parent;
        _group = NativeMethods.ma_sound_alloc();
        if (_group == IntPtr.Zero)
            throw new AudioException("group allocation failed");
        var result = NativeMethods.ma_sound_group_init(
            manager.Engine.Handle, 0, parent?._group ?? IntPtr.Zero, _group);
        if (result != 0)
        {
            NativeMethods.ma_sound_free(_group);
            _group = IntPtr.Zero;
            throw new AudioException("group initialization failed");
        }
    }

    internal IntPtr Handle => _group;

    /// <summary>The group as a graph node, for sounds routing through it.</summary>
    internal IntPtr NodePtr => NativeMethods.ma_sound_get_node_ptr(_group);

    private AudioEngine Engine => _manager.Engine;

    private IntPtr DownstreamNodePtr => _parent?.NodePtr ?? Engine.Endpoint;

    // ── Volume / pitch ──

    public float Volume
    {
        get => _baseVolume;
        set
        {
            _baseVolume = value;
            NativeMethods.ma_sound_set_volume(_group, value);
        }
    }

    /// <summary>1 = normal. Setting cancels a running tween.</summary>
    public float Pitch
    {
        get => _basePitch;
        set
        {
            _basePitch = value;
            _tweening = false;
            NativeMethods.ma_sound_set_pitch(_group, value);
        }
    }

    /// <summary>Smoothly interpolate the bus pitch (e.g. slow-motion on a
    /// master bus). Advances on <see cref="SoundManager.Tick"/>.</summary>
    public void TweenPitch(float target, TimeSpan duration, Easing easing = Easing.Linear)
    {
        if (_tweening)
            _basePitch = _pitchTween.Sample(out _);
        _pitchTween = PitchTween.Start(_basePitch, target, duration, easing);
        _tweening = true;
        AdvancePitchTween();
    }

    public void StopPitchTween()
    {
        if (_tweening)
            _basePitch = _pitchTween.Sample(out _);
        _tweening = false;
        NativeMethods.ma_sound_set_pitch(_group, _basePitch);
    }

    public bool IsPitchTweening => _tweening;

    internal void AdvancePitchTween()
    {
        if (!_tweening)
            return;
        _basePitch = _pitchTween.Sample(out var finished);
        NativeMethods.ma_sound_set_pitch(_group, _basePitch);
        if (finished)
            _tweening = false;
    }

    /// <summary>Start/stop the whole bus.</summary>
    public void Play() => NativeMethods.ma_sound_start(_group);

    public void Stop() => NativeMethods.ma_sound_stop(_group);

    // ── Effect chain ──

    // The ordered, kinds-repeatable chain (SetFxChain); wired ahead of
    // the classic slots.
    private readonly List<(SoundEffect Spec, IntPtr Node)> _custom = new();
    private IReadOnlyList<SoundEffect>? _customSpecs;

    /// <summary>An ordered effect chain with repeatable kinds — the
    /// general form of the classic slot effects below, which remain as
    /// the one-of-each convenience. Wiring: group → these (in order) →
    /// enabled slots → downstream. Parameter-only changes swap in
    /// place; shape changes rebuild. Null or empty removes the chain.
    /// Survives engine rebuilds.</summary>
    public void SetFxChain(IReadOnlyList<SoundEffect>? effects)
    {
        if (effects is null || effects.Count == 0)
        {
            DestroyCustomChain();
            _customSpecs = null;
            RebuildEffectChain();
            return;
        }
        var sameShape = _custom.Count == effects.Count;
        for (var i = 0; sameShape && i < effects.Count; i++)
            sameShape = FxNodes.SameShape(effects[i], _custom[i].Spec);
        if (sameShape)
        {
            for (var i = 0; i < effects.Count; i++)
                if (!effects[i].Equals(_custom[i].Spec))
                {
                    FxNodes.Reinit(Engine, _custom[i].Node, effects[i]);
                    _custom[i] = (effects[i], _custom[i].Node);
                }
        }
        else
        {
            DestroyCustomChain();
            foreach (var spec in effects)
            {
                var node = FxNodes.Create(Engine, spec);
                if (node != IntPtr.Zero)
                    _custom.Add((spec, node));
            }
            RebuildEffectChain();
        }
        _customSpecs = new List<SoundEffect>(effects);
    }

    private void DestroyCustomChain()
    {
        foreach (var (spec, node) in _custom)
        {
            NativeMethods.ma_node_detach_output_bus(node, 0);
            FxNodes.Destroy(node, spec);
        }
        _custom.Clear();
    }

    /// <summary>Rewire group → custom chain (in order) → enabled slot
    /// effects (fixed order) → downstream.</summary>
    private void RebuildEffectChain()
    {
        var groupNode = NodePtr;
        if (groupNode == IntPtr.Zero)
            return;

        var chain = new List<IntPtr>(_custom.Count + 7);
        foreach (var (_, node) in _custom)
            chain.Add(node);
        if (_filter != IntPtr.Zero) chain.Add(_filter);
        if (_eq != IntPtr.Zero) chain.Add(_eq);
        if (_distortion != IntPtr.Zero) chain.Add(_distortion);
        if (_vocoder != IntPtr.Zero) chain.Add(_vocoder);
        if (_disperser != IntPtr.Zero) chain.Add(_disperser);
        if (_delay != IntPtr.Zero) chain.Add(_delay);
        if (_reverb != IntPtr.Zero) chain.Add(_reverb);

        NativeMethods.ma_node_detach_output_bus(groupNode, 0);
        foreach (var node in chain)
            NativeMethods.ma_node_detach_output_bus(node, 0);

        var prev = groupNode;
        foreach (var node in chain)
        {
            NativeMethods.ma_node_attach_output_bus(prev, 0, node, 0);
            prev = node;
        }
        NativeMethods.ma_node_attach_output_bus(prev, 0, DownstreamNodePtr, 0);
    }

    private IntPtr CreateEffect(Func<IntPtr, uint, IntPtr> create, string name)
    {
        var node = create(Engine.NodeGraph, Engine.SampleRate);
        if (node == IntPtr.Zero)
            throw new AudioException($"failed to create {name} effect");
        return node;
    }

    // ── Reverb (convolution) ──

    public bool HasReverb => _reverb != IntPtr.Zero;

    /// <summary>Convolution reverb on this bus. <paramref name="diffuse"/>
    /// mono-sums the wet send by that amount before convolution — right
    /// for positioned game sources (the tail stops panning with the
    /// source) but it discards the send's side content: at 1 the sides
    /// never reach the room at all. Use 0 for music and other wide
    /// stereo material (channel-wise convolution, full side preserved).
    /// <paramref name="decay"/> reshapes the IR and costs a partition
    /// rebuild; the other parameters are live.</summary>
    public void EnableReverb(
        float wet, float dry, float predelayMs, float irGain, float width,
        float decay, float lowcutHz, float highcutHz, float diffuse)
    {
        if (_reverb == IntPtr.Zero)
        {
            _reverb = CreateEffect(NativeMethods.cosmos_reverb_create, "reverb");
            _reverbDecay = float.NaN;
            RebuildEffectChain();
        }
        SetReverbParams(wet, dry, predelayMs, irGain, width, decay, lowcutHz, highcutHz, diffuse);
    }

    public void DisableReverb()
    {
        if (_reverb == IntPtr.Zero) return;
        NativeMethods.cosmos_reverb_destroy(_reverb);
        _reverb = IntPtr.Zero;
        _reverbState = null;
        _reverbIrPath = null;
        RebuildEffectChain();
    }

    public void SetReverbParams(
        float wet, float dry, float predelayMs, float irGain, float width,
        float decay, float lowcutHz, float highcutHz, float diffuse)
    {
        if (_reverb == IntPtr.Zero) return;
        _reverbState = new ReverbState(wet, dry, predelayMs, irGain, width, decay, lowcutHz, highcutHz, diffuse);
        NativeMethods.ma_convreverb_node_set_wet(_reverb, wet);
        NativeMethods.ma_convreverb_node_set_dry(_reverb, dry);
        NativeMethods.ma_convreverb_node_set_predelay(_reverb, predelayMs);
        NativeMethods.ma_convreverb_node_set_ir_gain(_reverb, irGain);
        NativeMethods.ma_convreverb_node_set_width(_reverb, width);
        NativeMethods.ma_convreverb_node_set_lowcut(_reverb, lowcutHz);
        NativeMethods.ma_convreverb_node_set_highcut(_reverb, highcutHz);
        NativeMethods.ma_convreverb_node_set_diffuse(_reverb, diffuse);
        // Decay reshapes the IR (a partition rebuild) — only apply on a
        // real change so tweaking the cheap params stays cheap.
        if (float.IsNaN(_reverbDecay) || MathF.Abs(_reverbDecay - decay) > 1e-4f)
        {
            NativeMethods.ma_convreverb_node_set_decay(_reverb, decay);
            _reverbDecay = decay;
        }
    }

    /// <summary>Load a custom impulse response; false if reverb is off or
    /// the file failed to load.</summary>
    public bool SetReverbIr(string path)
    {
        if (_reverb == IntPtr.Zero || NativeMethods.ma_convreverb_node_load_ir_file(_reverb, path) != 0)
            return false;
        _reverbIrPath = path;
        return true;
    }

    /// <summary>Restore the built-in synthetic IR.</summary>
    public bool ResetReverbIr()
    {
        if (_reverb == IntPtr.Zero || NativeMethods.ma_convreverb_node_load_default_ir(_reverb) != 0)
            return false;
        _reverbIrPath = null;
        return true;
    }

    // ── EQ (3-band) ──

    public bool HasEq => _eq != IntPtr.Zero;

    public void EnableEq(
        float lowGainDb, float midGainDb, float highGainDb,
        float lowFreq, float midFreq, float highFreq, float midQ)
    {
        if (_eq == IntPtr.Zero)
        {
            _eq = CreateEffect(NativeMethods.cosmos_eq_create, "eq");
            RebuildEffectChain();
        }
        SetEqParams(lowGainDb, midGainDb, highGainDb, lowFreq, midFreq, highFreq, midQ);
    }

    public void DisableEq()
    {
        if (_eq == IntPtr.Zero) return;
        NativeMethods.cosmos_eq_destroy(_eq);
        _eq = IntPtr.Zero;
        _eqState = null;
        RebuildEffectChain();
    }

    public void SetEqParams(
        float lowGainDb, float midGainDb, float highGainDb,
        float lowFreq, float midFreq, float highFreq, float midQ)
    {
        if (_eq == IntPtr.Zero) return;
        _eqState = new EqState(lowGainDb, midGainDb, highGainDb, lowFreq, midFreq, highFreq, midQ);
        NativeMethods.ma_eq_node_set_low_freq(_eq, lowFreq);
        NativeMethods.ma_eq_node_set_mid_freq(_eq, midFreq);
        NativeMethods.ma_eq_node_set_high_freq(_eq, highFreq);
        NativeMethods.ma_eq_node_set_low_gain(_eq, lowGainDb);
        NativeMethods.ma_eq_node_set_mid_gain(_eq, midGainDb);
        NativeMethods.ma_eq_node_set_high_gain(_eq, highGainDb);
        NativeMethods.ma_eq_node_set_mid_q(_eq, midQ);
    }

    // ── Multimode filter ──

    public bool HasFilter => _filter != IntPtr.Zero;

    public void EnableFilter(FilterMode mode, float freq, float q, float gainDb)
    {
        if (_filter == IntPtr.Zero)
        {
            _filter = CreateEffect(NativeMethods.cosmos_filter_create, "filter");
            RebuildEffectChain();
        }
        SetFilterParams(mode, freq, q, gainDb);
    }

    public void DisableFilter()
    {
        if (_filter == IntPtr.Zero) return;
        NativeMethods.cosmos_filter_destroy(_filter);
        _filter = IntPtr.Zero;
        _filterState = null;
        RebuildEffectChain();
    }

    public void SetFilterParams(FilterMode mode, float freq, float q, float gainDb)
    {
        if (_filter == IntPtr.Zero) return;
        _filterState = new FilterState(mode, freq, q, gainDb);
        NativeMethods.ma_filter_node_set_mode(_filter, (int)mode);
        NativeMethods.ma_filter_node_set_freq(_filter, freq);
        NativeMethods.ma_filter_node_set_q(_filter, q);
        NativeMethods.ma_filter_node_set_gain(_filter, gainDb);
    }

    // ── Distortion ──

    public bool HasDistortion => _distortion != IntPtr.Zero;

    public void EnableDistortion(float drive, float toneHz, float wet, float dry)
    {
        if (_distortion == IntPtr.Zero)
        {
            _distortion = CreateEffect(NativeMethods.cosmos_distortion_create, "distortion");
            RebuildEffectChain();
        }
        SetDistortionParams(drive, toneHz, wet, dry);
    }

    public void DisableDistortion()
    {
        if (_distortion == IntPtr.Zero) return;
        NativeMethods.cosmos_distortion_destroy(_distortion);
        _distortion = IntPtr.Zero;
        _distortionState = null;
        RebuildEffectChain();
    }

    public void SetDistortionParams(float drive, float toneHz, float wet, float dry)
    {
        if (_distortion == IntPtr.Zero) return;
        _distortionState = new DistortionState(drive, toneHz, wet, dry);
        NativeMethods.ma_distortion_node_set_drive(_distortion, drive);
        NativeMethods.ma_distortion_node_set_tone(_distortion, toneHz);
        NativeMethods.ma_distortion_node_set_wet(_distortion, wet);
        NativeMethods.ma_distortion_node_set_dry(_distortion, dry);
    }

    // ── Vocoder ──

    public bool HasVocoder => _vocoder != IntPtr.Zero;

    public void EnableVocoder(
        int bands, VocoderCarrier carrier, float carrierFreq,
        float attackMs, float releaseMs, float wet, float dry,
        bool randOn = false, float randRate = 0.0f, float randDepth = 0.0f,
        float formant = 1.0f, float spread = 0.0f, float sibilance = 0.0f)
    {
        if (_vocoder == IntPtr.Zero)
        {
            _vocoder = CreateEffect(NativeMethods.cosmos_vocoder_create, "vocoder");
            RebuildEffectChain();
        }
        SetVocoderParams(bands, carrier, carrierFreq, attackMs, releaseMs, wet, dry,
            randOn, randRate, randDepth, formant, spread, sibilance);
    }

    public void DisableVocoder()
    {
        if (_vocoder == IntPtr.Zero) return;
        NativeMethods.cosmos_vocoder_destroy(_vocoder);
        _vocoder = IntPtr.Zero;
        _vocoderState = null;
        RebuildEffectChain();
    }

    public void SetVocoderParams(
        int bands, VocoderCarrier carrier, float carrierFreq,
        float attackMs, float releaseMs, float wet, float dry,
        bool randOn = false, float randRate = 0.0f, float randDepth = 0.0f,
        float formant = 1.0f, float spread = 0.0f, float sibilance = 0.0f)
    {
        if (_vocoder == IntPtr.Zero) return;
        _vocoderState = new VocoderState(bands, carrier, carrierFreq, attackMs, releaseMs,
            wet, dry, randOn, randRate, randDepth, formant, spread, sibilance);
        NativeMethods.ma_vocoder_node_set_bands(_vocoder, bands);
        NativeMethods.ma_vocoder_node_set_carrier(_vocoder, (int)carrier);
        NativeMethods.ma_vocoder_node_set_carrier_freq(_vocoder, carrierFreq);
        NativeMethods.ma_vocoder_node_set_attack(_vocoder, attackMs);
        NativeMethods.ma_vocoder_node_set_release(_vocoder, releaseMs);
        NativeMethods.ma_vocoder_node_set_wet(_vocoder, wet);
        NativeMethods.ma_vocoder_node_set_dry(_vocoder, dry);
        NativeMethods.ma_vocoder_node_set_rand(_vocoder, randOn ? 1 : 0);
        NativeMethods.ma_vocoder_node_set_rand_rate(_vocoder, randRate);
        NativeMethods.ma_vocoder_node_set_rand_depth(_vocoder, randDepth);
        NativeMethods.ma_vocoder_node_set_formant(_vocoder, formant);
        NativeMethods.ma_vocoder_node_set_spread(_vocoder, spread);
        NativeMethods.ma_vocoder_node_set_sibilance(_vocoder, sibilance);
    }

    // ── Disperser ──

    public bool HasDisperser => _disperser != IntPtr.Zero;

    public void EnableDisperser(float freq, float q, int stages)
    {
        if (_disperser == IntPtr.Zero)
        {
            _disperser = CreateEffect(NativeMethods.cosmos_disperser_create, "disperser");
            RebuildEffectChain();
        }
        SetDisperserParams(freq, q, stages);
    }

    public void DisableDisperser()
    {
        if (_disperser == IntPtr.Zero) return;
        NativeMethods.cosmos_disperser_destroy(_disperser);
        _disperser = IntPtr.Zero;
        _disperserState = null;
        RebuildEffectChain();
    }

    public void SetDisperserParams(float freq, float q, int stages)
    {
        if (_disperser == IntPtr.Zero) return;
        _disperserState = new DisperserState(freq, q, stages);
        NativeMethods.ma_disperser_node_set_freq(_disperser, freq);
        NativeMethods.ma_disperser_node_set_q(_disperser, q);
        NativeMethods.ma_disperser_node_set_stages(_disperser, stages);
    }

    // ── Delay / echo ──

    public bool HasDelay => _delay != IntPtr.Zero;

    public void EnableDelay(float delayMs, float feedback, float wet, float dry)
    {
        if (_delay == IntPtr.Zero)
        {
            _delay = CreateEffect(NativeMethods.cosmos_delay_create, "delay");
            RebuildEffectChain();
        }
        SetDelayParams(delayMs, feedback, wet, dry);
    }

    public void DisableDelay()
    {
        if (_delay == IntPtr.Zero) return;
        NativeMethods.cosmos_delay_destroy(_delay);
        _delay = IntPtr.Zero;
        _delayState = null;
        RebuildEffectChain();
    }

    public void SetDelayParams(float delayMs, float feedback, float wet, float dry)
    {
        if (_delay == IntPtr.Zero) return;
        _delayState = new DelayState(delayMs, feedback, wet, dry);
        NativeMethods.ma_delay_fx_set_delay_ms(_delay, delayMs);
        NativeMethods.ma_delay_fx_set_feedback(_delay, feedback);
        NativeMethods.ma_delay_fx_set_wet(_delay, wet);
        NativeMethods.ma_delay_fx_set_dry(_delay, dry);
    }

    // ── Engine rebuild (SoundManager.Reconfigure) ──

    /// <summary>Release every native resource ahead of an engine swap,
    /// keeping the managed state (volume, pitch, tween, effect
    /// parameters) that <see cref="RecreateNative"/> replays. Children
    /// release before parents; sounds have already released.</summary>
    internal void ReleaseNative()
    {
        _rebuildStopped = NativeMethods.ma_sound_is_playing(_group) == 0;
        DestroyEffects();
        NativeMethods.ma_sound_group_uninit(_group);
    }

    /// <summary>Re-init against the manager's current engine and replay
    /// volume, pitch, transport, and the effect chain. Parents recreate
    /// before children.</summary>
    internal void RecreateNative()
    {
        var result = NativeMethods.ma_sound_group_init(
            Engine.Handle, 0, _parent?._group ?? IntPtr.Zero, _group);
        if (result != 0)
            throw new AudioException("group re-initialization failed");
        NativeMethods.ma_sound_set_volume(_group, _baseVolume);
        NativeMethods.ma_sound_set_pitch(_group, _basePitch);
        if (_rebuildStopped)
            NativeMethods.ma_sound_stop(_group);

        // Recreate enabled effects in chain order; Enable re-stores the
        // same parameter state it replays.
        if (_filterState is { } filter)
            EnableFilter(filter.Mode, filter.Freq, filter.Q, filter.GainDb);
        if (_eqState is { } eq)
            EnableEq(eq.LowGainDb, eq.MidGainDb, eq.HighGainDb, eq.LowFreq, eq.MidFreq, eq.HighFreq, eq.MidQ);
        if (_distortionState is { } distortion)
            EnableDistortion(distortion.Drive, distortion.ToneHz, distortion.Wet, distortion.Dry);
        if (_vocoderState is { } vocoder)
            EnableVocoder(vocoder.Bands, vocoder.Carrier, vocoder.CarrierFreq,
                vocoder.AttackMs, vocoder.ReleaseMs, vocoder.Wet, vocoder.Dry,
                vocoder.RandOn, vocoder.RandRate, vocoder.RandDepth,
                vocoder.Formant, vocoder.Spread, vocoder.Sibilance);
        if (_disperserState is { } disperser)
            EnableDisperser(disperser.Freq, disperser.Q, disperser.Stages);
        if (_delayState is { } delay)
            EnableDelay(delay.DelayMs, delay.Feedback, delay.Wet, delay.Dry);
        if (_reverbState is { } reverb)
        {
            EnableReverb(reverb.Wet, reverb.Dry, reverb.PredelayMs, reverb.IrGain, reverb.Width,
                reverb.Decay, reverb.LowcutHz, reverb.HighcutHz, reverb.Diffuse);
            // A vanished IR file falls back to the built-in default.
            if (_reverbIrPath is { } irPath && !SetReverbIr(irPath))
                _reverbIrPath = null;
        }
        if (_customSpecs is { } customs)
            SetFxChain(customs);
    }

    private void DestroyEffects()
    {
        DestroyCustomChain();
        if (_filter != IntPtr.Zero) NativeMethods.cosmos_filter_destroy(_filter);
        if (_eq != IntPtr.Zero) NativeMethods.cosmos_eq_destroy(_eq);
        if (_distortion != IntPtr.Zero) NativeMethods.cosmos_distortion_destroy(_distortion);
        if (_vocoder != IntPtr.Zero) NativeMethods.cosmos_vocoder_destroy(_vocoder);
        if (_disperser != IntPtr.Zero) NativeMethods.cosmos_disperser_destroy(_disperser);
        if (_delay != IntPtr.Zero) NativeMethods.cosmos_delay_destroy(_delay);
        if (_reverb != IntPtr.Zero) NativeMethods.cosmos_reverb_destroy(_reverb);
        _filter = _eq = _distortion = _vocoder = _disperser = _delay = _reverb = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (IsDisposed)
            return;
        IsDisposed = true;

        // Tear the effects out of the graph before the group goes away.
        var hadEffects = _custom.Count > 0 || _filter != IntPtr.Zero || _eq != IntPtr.Zero
            || _distortion != IntPtr.Zero || _vocoder != IntPtr.Zero
            || _disperser != IntPtr.Zero || _delay != IntPtr.Zero
            || _reverb != IntPtr.Zero;
        DestroyEffects();
        _customSpecs = null;
        if (hadEffects)
            RebuildEffectChain();

        if (_group != IntPtr.Zero)
        {
            NativeMethods.ma_sound_group_uninit(_group);
            NativeMethods.ma_sound_free(_group);
            _group = IntPtr.Zero;
        }
    }

    // Effect parameter snapshots, one per chain slot.
    private readonly record struct FilterState(FilterMode Mode, float Freq, float Q, float GainDb);

    private readonly record struct EqState(
        float LowGainDb, float MidGainDb, float HighGainDb,
        float LowFreq, float MidFreq, float HighFreq, float MidQ);

    private readonly record struct DistortionState(float Drive, float ToneHz, float Wet, float Dry);

    private readonly record struct VocoderState(
        int Bands, VocoderCarrier Carrier, float CarrierFreq,
        float AttackMs, float ReleaseMs, float Wet, float Dry,
        bool RandOn, float RandRate, float RandDepth,
        float Formant, float Spread, float Sibilance);

    private readonly record struct DisperserState(float Freq, float Q, int Stages);

    private readonly record struct DelayState(float DelayMs, float Feedback, float Wet, float Dry);

    private readonly record struct ReverbState(
        float Wet, float Dry, float PredelayMs, float IrGain, float Width,
        float Decay, float LowcutHz, float HighcutHz, float Diffuse);
}
