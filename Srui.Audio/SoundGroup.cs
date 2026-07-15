namespace Srui.Audio;

/// <summary>Multimode filter types for <see cref="SoundEffect.Filter"/>.</summary>
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
/// pitch, and an optional effect chain (<see cref="SetFxChain"/> — an
/// ordered <see cref="SoundEffect"/> list, kinds repeatable) to
/// everything routed through it: group → effects in order → downstream.
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

    // Spatial contribution from an owning SoundEntity: attenuation
    // multiplies the user volume, pan applies directly, and the pitch
    // delta (behind-pitch-decrease) adds to the user pitch. Neutral for
    // plain buses.
    private float _spatialGain = 1.0f;
    private float _spatialPan;
    private float _spatialPitchDelta;

    /// <summary>The SoundEntity this group belongs to, when it is an
    /// entity's bus — so Sound.Play can flush the entity's pending
    /// spatialization before the first sample mixes.</summary>
    internal SoundEntity? Entity { get; set; }

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

    /// <summary>Where this bus's output ultimately lands — for a
    /// SoundEntity wiring its convolver ahead of it.</summary>
    internal IntPtr DownstreamPtr => DownstreamNodePtr;

    // An owning SoundEntity's convolver. When set, the bus (after its
    // effect chain) routes through it instead of straight downstream.
    internal IntPtr SpatialOutput { get; private set; }

    /// <summary>Route the bus through `node` (zero restores the direct
    /// path) and rewire. The caller owns the node and its attachment to
    /// the downstream bus.</summary>
    internal void SetSpatialOutput(IntPtr node)
    {
        SpatialOutput = node;
        RebuildEffectChain();
    }

    /// <summary>Forget the spatial output without rewiring — for engine
    /// rebuilds, where the natives are already gone.</summary>
    internal void ClearSpatialOutput() => SpatialOutput = IntPtr.Zero;

    // ── Volume / pitch ──

    public float Volume
    {
        get => _baseVolume;
        set
        {
            _baseVolume = value;
            NativeMethods.ma_sound_set_volume(_group, value * _spatialGain);
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
            NativeMethods.ma_sound_set_pitch(_group, value + _spatialPitchDelta);
        }
    }

    /// <summary>Apply an entity's spatial state to the bus in one write
    /// per changed parameter (the entity computes; the group owns the
    /// natives so user volume/pitch and spatial state compose).</summary>
    internal void SetSpatial(float gain, float pan, float pitchDelta)
    {
        if (gain != _spatialGain)
        {
            _spatialGain = gain;
            NativeMethods.ma_sound_set_volume(_group, _baseVolume * gain);
        }
        if (pan != _spatialPan)
        {
            _spatialPan = pan;
            NativeMethods.ma_sound_set_pan(_group, pan);
        }
        if (pitchDelta != _spatialPitchDelta)
        {
            _spatialPitchDelta = pitchDelta;
            NativeMethods.ma_sound_set_pitch(_group, _basePitch + pitchDelta);
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
        NativeMethods.ma_sound_set_pitch(_group, _basePitch + _spatialPitchDelta);
    }

    public bool IsPitchTweening => _tweening;

    internal void AdvancePitchTween()
    {
        if (!_tweening)
            return;
        _basePitch = _pitchTween.Sample(out var finished);
        NativeMethods.ma_sound_set_pitch(_group, _basePitch + _spatialPitchDelta);
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

    /// <summary>Rewire group → chain (in order) → downstream.</summary>
    private void RebuildEffectChain()
    {
        var groupNode = NodePtr;
        if (groupNode == IntPtr.Zero)
            return;

        NativeMethods.ma_node_detach_output_bus(groupNode, 0);
        foreach (var (_, node) in _custom)
            NativeMethods.ma_node_detach_output_bus(node, 0);

        var prev = groupNode;
        foreach (var (_, node) in _custom)
        {
            NativeMethods.ma_node_attach_output_bus(prev, 0, node, 0);
            prev = node;
        }
        NativeMethods.ma_node_attach_output_bus(
            prev, 0, SpatialOutput != IntPtr.Zero ? SpatialOutput : DownstreamNodePtr, 0);
    }

    // ── Engine rebuild (SoundManager.Reconfigure) ──

    /// <summary>Release every native resource ahead of an engine swap,
    /// keeping the managed state (volume, pitch, tween, effect
    /// parameters) that <see cref="RecreateNative"/> replays. Children
    /// release before parents; sounds have already released.</summary>
    internal void ReleaseNative()
    {
        _rebuildStopped = NativeMethods.ma_sound_is_playing(_group) == 0;
        DestroyCustomChain();
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
        // Spatial state resets with the natives; the owning entity (if
        // any) recomputes and reapplies on its own recreate pass.
        _spatialGain = 1.0f;
        _spatialPan = 0.0f;
        _spatialPitchDelta = 0.0f;
        NativeMethods.ma_sound_set_volume(_group, _baseVolume);
        NativeMethods.ma_sound_set_pitch(_group, _basePitch);
        if (_rebuildStopped)
            NativeMethods.ma_sound_stop(_group);

        // The chain recreates from its replayed specs.
        if (_customSpecs is { } customs)
            SetFxChain(customs);
    }

    public void Dispose()
    {
        if (IsDisposed)
            return;
        IsDisposed = true;

        // Tear the effects out of the graph before the group goes away.
        var hadEffects = _custom.Count > 0;
        DestroyCustomChain();
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

}
