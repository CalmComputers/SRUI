using System.Runtime.InteropServices;

namespace Srui.Audio;

/// <summary>
/// A playable sound: a stream with volume, pitch, pan, and an optional
/// per-sound effect chain. Sounds carry no position — a sound becomes
/// spatial by being created in a <see cref="SoundEntity"/>'s group, which
/// spatializes everything the entity emits as one source. Create via
/// <see cref="SoundManager.CreateSound"/>.
/// </summary>
public sealed unsafe class Sound : IDisposable
{
    private readonly SoundManager _manager;
    private readonly SoundGroup? _group;
    private IntPtr _sound;
    private bool _loaded;

    // PCM ownership for stretched/reversed loads.
    private float* _pcm;
    private IntPtr _bufferRef;

    private float _baseVolume = 1.0f;
    private float _basePitch = 1.0f;
    private PitchTween _pitchTween;
    private bool _tweening;

    // What Load*/… was last called with, so an engine rebuild
    // (SoundManager.Reconfigure) can replay it.
    private enum LoadKind : byte { None, Plain, Stretched, Reversed, Streamed, Pull }
    private LoadKind _loadKind;
    private bool _loadAsync;
    private string? _loadPath;
    private float _stretchFactor;

    // Media-playback state: a managed decoder feeding the sound, and
    // the per-sound effect chain.
    private PullAudioSource? _pullSource;
    private FxChain? _fx;
    private IReadOnlyList<SoundEffect>? _fxSpecs;

    // Native-only state snapshotted by ReleaseNative for RecreateNative.
    private bool _rebuildPlaying;
    private ulong _rebuildCursorMs;
    private bool _rebuildLooping;
    private float _rebuildPan;

    public bool IsDisposed { get; private set; }

    internal Sound(SoundManager manager, SoundGroup? group)
    {
        _manager = manager;
        _group = group;
        _sound = NativeMethods.ma_sound_alloc();
        if (_sound == IntPtr.Zero)
            throw new AudioException("sound allocation failed");
    }

    private AudioEngine Engine => _manager.Engine;

    private IntPtr GroupPtr => _group?.Handle ?? IntPtr.Zero;

    // ── Loading ──

    /// <summary>Load from a file. Decoded data is cached and shared when
    /// the same file is loaded more than once. With
    /// <paramref name="asyncLoad"/>, the decode runs on the resource
    /// manager's job thread and this call returns immediately: playback
    /// starts as soon as audio is available (instantly for an
    /// already-cached file), but duration reads report 0 until the
    /// decode registers — don't pass it for a sound whose length drives
    /// timing decisions.</summary>
    public void Load(string filename, bool asyncLoad = false)
    {
        UnloadCurrent();
        uint flags = NativeMethods.SoundFlagDecode
            | (asyncLoad ? NativeMethods.SoundFlagAsync : 0u);
        var result = NativeMethods.ma_sound_init_from_file(
            Engine.Handle, filename, flags, GroupPtr, IntPtr.Zero, _sound);
        if (result != 0)
            throw new AudioException($"failed to load '{filename}'");
        _loadKind = LoadKind.Plain;
        _loadAsync = asyncLoad;
        _loadPath = filename;
        FinishLoad();
    }

    /// <summary>Load from a file, streamed: decoded incrementally off
    /// disk instead of held whole in memory — the media-playback path
    /// for hours-long files. Seeking works; the decode cache is not
    /// involved.</summary>
    public void LoadStreamed(string filename)
    {
        UnloadCurrent();
        var result = NativeMethods.ma_sound_init_from_file(
            Engine.Handle, filename, NativeMethods.SoundFlagStream, GroupPtr, IntPtr.Zero, _sound);
        if (result != 0)
            throw new AudioException($"failed to load '{filename}'");
        _loadKind = LoadKind.Streamed;
        _loadPath = filename;
        FinishLoad();
    }

    /// <summary>Load from a managed decoder (<see cref="PullAudioSource"/>).
    /// The sound takes ownership of the source and disposes it on
    /// unload. Such a sound does not survive an engine rebuild
    /// (SoundManager.Reconfigure) — the owner reloads.</summary>
    public void LoadPull(PullAudioSource source)
    {
        UnloadCurrent();
        var result = NativeMethods.ma_sound_init_from_data_source(
            Engine.Handle, source.Handle, 0, GroupPtr, _sound);
        if (result != 0)
        {
            source.Dispose();
            throw new AudioException("failed to load from the pull source");
        }
        _pullSource = source;
        _loadKind = LoadKind.Pull;
        _loadPath = null;
        FinishLoad();
    }

    /// <summary>Header-only duration read — no device, no full decode.
    /// 0 when unknown or unreadable (or the format needs a managed
    /// decoder). The playlist-metadata probe.</summary>
    public static ulong ProbeDurationMs(string path) =>
        NativeMethods.cosmos_probe_duration_ms(path);

    /// <summary>Load and time-stretch by `factor` with pitch preserved
    /// (0.5..3.0 reasonable; &gt;1 = longer/slower). The stretched PCM is
    /// held in native memory for the life of the sound.</summary>
    public void LoadStretched(string filename, float factor)
    {
        UnloadCurrent();
        var pcm = DecodeToManaged(filename, out var channels);
        var stretched = OlaStretch.Stretch(pcm, (int)channels, Engine.SampleRate, factor);
        if (stretched.Length == 0)
            throw new AudioException($"failed to stretch '{filename}'");
        InitFromPcm(stretched, channels, filename);
        _loadKind = LoadKind.Stretched;
        _loadPath = filename;
        _stretchFactor = factor;
    }

    /// <summary>Load with the sample frames reversed in memory.</summary>
    public void LoadReversed(string filename)
    {
        UnloadCurrent();
        var pcm = DecodeToManaged(filename, out var channels);
        var ch = (int)channels;
        var frames = pcm.Length / ch;
        for (var i = 0; i < frames / 2; i++)
        {
            var j = frames - 1 - i;
            for (var c = 0; c < ch; c++)
                (pcm[i * ch + c], pcm[j * ch + c]) = (pcm[j * ch + c], pcm[i * ch + c]);
        }
        InitFromPcm(pcm, channels, filename);
        _loadKind = LoadKind.Reversed;
        _loadPath = filename;
    }

    private float[] DecodeToManaged(string filename, out uint channels)
    {
        var native = NativeMethods.cosmos_decode_file(
            filename, Engine.SampleRate, out channels, out _, out var frames);
        if (native == null || channels == 0 || frames == 0)
        {
            if (native != null) NativeMethods.cosmos_free(native);
            throw new AudioException($"failed to decode '{filename}'");
        }
        var samples = new float[checked((int)(frames * channels))];
        new ReadOnlySpan<float>(native, samples.Length).CopyTo(samples);
        NativeMethods.cosmos_free(native);
        return samples;
    }

    private void InitFromPcm(float[] pcm, uint channels, string filename)
    {
        // Copy into native memory so the address is stable for miniaudio.
        var bytes = (nuint)pcm.Length * sizeof(float);
        _pcm = (float*)NativeMemory.Alloc(bytes);
        pcm.AsSpan().CopyTo(new Span<float>(_pcm, pcm.Length));

        var frames = (ulong)(pcm.Length / (int)channels);
        _bufferRef = NativeMethods.cosmos_buffer_ref_create(channels, _pcm, frames);
        if (_bufferRef == IntPtr.Zero)
        {
            FreePcm();
            throw new AudioException($"failed to create buffer for '{filename}'");
        }

        var result = NativeMethods.ma_sound_init_from_data_source(
            Engine.Handle, _bufferRef, 0, GroupPtr, _sound);
        if (result != 0)
        {
            NativeMethods.cosmos_buffer_ref_destroy(_bufferRef);
            _bufferRef = IntPtr.Zero;
            FreePcm();
            throw new AudioException($"failed to load '{filename}'");
        }
        FinishLoad();
    }

    private void FinishLoad()
    {
        _loaded = true;
        // Spatialization is entity-level (the group), never miniaudio's.
        NativeMethods.ma_sound_set_spatialization_enabled(_sound, 0);
        // A load resets the natives; replay the managed mix state.
        NativeMethods.ma_sound_set_volume(_sound, _baseVolume);
        NativeMethods.ma_sound_set_pitch(_sound, _basePitch);
    }

    private void UnloadCurrent()
    {
        // The chain references this sound's node; tear it down first.
        // The specs survive so a reload (engine rebuild) re-applies.
        _fx?.Dispose();
        _fx = null;
        if (_loaded)
        {
            NativeMethods.ma_sound_uninit(_sound);
            _loaded = false;
        }
        // Only after the sound stopped pulling from it.
        _pullSource?.Dispose();
        _pullSource = null;
        if (_bufferRef != IntPtr.Zero)
        {
            NativeMethods.cosmos_buffer_ref_destroy(_bufferRef);
            _bufferRef = IntPtr.Zero;
        }
        FreePcm();
    }

    /// <summary>Apply an ordered per-sound effect chain (kinds
    /// repeatable), replacing the previous one; parameter-only changes
    /// swap coefficients glitch-free. Null or empty removes the chain.
    /// Steam Audio effects degrade to passthrough when phonon is
    /// unavailable. Re-applied automatically across engine rebuilds;
    /// call again after any Load* (a load resets the wiring).</summary>
    public void SetFxChain(IReadOnlyList<SoundEffect>? effects)
    {
        if (effects is null || effects.Count == 0)
        {
            _fx?.Dispose();
            _fx = null;
            _fxSpecs = null;
            return;
        }
        if (!_loaded)
            throw new AudioException("load a sound before applying an effect chain");
        _fx ??= new FxChain(
            Engine,
            NativeMethods.ma_sound_get_node_ptr(_sound),
            _group?.NodePtr ?? Engine.Endpoint);
        _fx.Apply(effects);
        _fxSpecs = new List<SoundEffect>(effects);
    }

    private void FreePcm()
    {
        if (_pcm != null)
        {
            NativeMemory.Free(_pcm);
            _pcm = null;
        }
    }

    // ── Transport ──

    public void Play()
    {
        RequireLoaded();
        // An entity sound applies the entity's pending spatialization
        // before its first buffer mixes; the deferred (per-Tick) apply
        // would land late and the correction would click.
        _group?.Entity?.ApplyPending();
        if (NativeMethods.ma_sound_start(_sound) != 0)
            throw new AudioException("playback failed");
    }

    /// <summary>Stop and rewind to the beginning.</summary>
    public void Stop()
    {
        RequireLoaded();
        NativeMethods.ma_sound_stop(_sound);
        NativeMethods.ma_sound_seek_to_pcm_frame(_sound, 0);
    }

    /// <summary>Stop without rewinding.</summary>
    public void Pause()
    {
        RequireLoaded();
        NativeMethods.ma_sound_stop(_sound);
    }

    public bool IsLoaded => _loaded;
    public bool IsPlaying => _loaded && NativeMethods.ma_sound_is_playing(_sound) != 0;
    public bool AtEnd => _loaded && NativeMethods.ma_sound_at_end(_sound) != 0;

    public bool Looping
    {
        get => _loaded && NativeMethods.ma_sound_is_looping(_sound) != 0;
        set
        {
            if (_loaded) NativeMethods.ma_sound_set_looping(_sound, value ? 1u : 0u);
        }
    }

    /// <summary>The rate of the sound's cursor/seek/length frames: the
    /// data source's native sample rate, not the engine's — the decode
    /// cache keeps files at their native rate and pull sources report
    /// their own. Buffer refs report 0 (their PCM was decoded at the
    /// engine rate) and an async load reports nothing until the decode
    /// registers; both fall back to the engine rate.</summary>
    private uint FrameRate
    {
        get
        {
            if (NativeMethods.ma_sound_get_data_format(
                    _sound, IntPtr.Zero, IntPtr.Zero, out var rate, IntPtr.Zero, 0) != 0
                || rate == 0)
                return Engine.SampleRate;
            return rate;
        }
    }

    /// <summary>Playback position in milliseconds.</summary>
    public ulong PlaybackPosition
    {
        get
        {
            if (!_loaded) return 0;
            if (NativeMethods.ma_sound_get_cursor_in_pcm_frames(_sound, out var cursor) != 0)
                return 0;
            var sr = FrameRate;
            return sr == 0 ? 0 : cursor * 1000 / sr;
        }
        set
        {
            if (!_loaded) return;
            var sr = FrameRate;
            if (sr == 0) return;
            NativeMethods.ma_sound_seek_to_pcm_frame(_sound, value * sr / 1000);
        }
    }

    /// <summary>Total length in milliseconds.</summary>
    public ulong Length
    {
        get
        {
            if (!_loaded) return 0;
            if (NativeMethods.ma_sound_get_length_in_pcm_frames(_sound, out var frames) != 0)
                return 0;
            var sr = FrameRate;
            return sr == 0 ? 0 : frames * 1000 / sr;
        }
    }

    // ── Mix parameters ──

    /// <summary>Base volume (0..1), before the group's bus volume and
    /// any entity attenuation.</summary>
    public float BaseVolume
    {
        get => _baseVolume;
        set
        {
            _baseVolume = value;
            if (_loaded) NativeMethods.ma_sound_set_volume(_sound, value);
        }
    }

    /// <summary>Base pitch (1 = normal). Setting cancels a running tween.</summary>
    public float Pitch
    {
        get => _basePitch;
        set
        {
            _basePitch = value;
            _tweening = false;
            if (_loaded) NativeMethods.ma_sound_set_pitch(_sound, value);
        }
    }

    /// <summary>Interpolate pitch to `target` over `duration`. Advances
    /// on <see cref="SoundManager.Tick"/>.</summary>
    public void TweenPitch(float target, TimeSpan duration, Easing easing = Easing.Linear)
    {
        if (_tweening)
            _basePitch = _pitchTween.Sample(out _);
        _pitchTween = PitchTween.Start(_basePitch, target, duration, easing);
        _tweening = true;
        AdvancePitchTween();
    }

    /// <summary>Cancel the tween; pitch holds at its current value.</summary>
    public void StopPitchTween()
    {
        if (_tweening)
            _basePitch = _pitchTween.Sample(out _);
        _tweening = false;
        if (_loaded) NativeMethods.ma_sound_set_pitch(_sound, _basePitch);
    }

    public bool IsPitchTweening => _tweening;

    internal void AdvancePitchTween()
    {
        if (!_tweening)
            return;
        _basePitch = _pitchTween.Sample(out var finished);
        if (_loaded) NativeMethods.ma_sound_set_pitch(_sound, _basePitch);
        if (finished)
            _tweening = false;
    }

    /// <summary>Direct pan (-1..1). For a sound in an entity group the
    /// entity's spatial pan applies at the bus, on top of this.</summary>
    public float Pan
    {
        get => _loaded ? NativeMethods.ma_sound_get_pan(_sound) : 0.0f;
        set
        {
            if (_loaded) NativeMethods.ma_sound_set_pan(_sound, value);
        }
    }

    private void RequireLoaded()
    {
        if (!_loaded)
            throw new AudioException("sound not loaded");
    }

    // ── Engine rebuild (SoundManager.Reconfigure) ──

    /// <summary>Release every native resource ahead of an engine swap,
    /// snapshotting the native-only state (transport, cursor, looping,
    /// pan) that <see cref="RecreateNative"/> replays. Managed state —
    /// mix parameters, effect specs, tweens — survives in place.</summary>
    internal void ReleaseNative()
    {
        _rebuildPlaying = IsPlaying;
        _rebuildCursorMs = PlaybackPosition;
        _rebuildLooping = Looping;
        _rebuildPan = Pan;
        UnloadCurrent();
    }

    /// <summary>Reload against the manager's current engine and replay
    /// the snapshot. A sound whose source no longer loads is left
    /// unloaded (IsLoaded false) rather than failing the rebuild.</summary>
    internal void RecreateNative()
    {
        if (_loadKind == LoadKind.None)
            return;
        try
        {
            switch (_loadKind)
            {
                case LoadKind.Plain:
                    Load(_loadPath!, _loadAsync);
                    break;
                case LoadKind.Stretched:
                    LoadStretched(_loadPath!, _stretchFactor);
                    break;
                case LoadKind.Reversed:
                    LoadReversed(_loadPath!);
                    break;
                case LoadKind.Streamed:
                    LoadStreamed(_loadPath!);
                    break;
                case LoadKind.Pull:
                    // A managed decoder cannot be replayed from here;
                    // the owner reloads. The sound stays unloaded.
                    return;
            }
        }
        catch (AudioException)
        {
            return;
        }
        if (_fxSpecs is { } specs)
            SetFxChain(specs);
        Looping = _rebuildLooping;
        PlaybackPosition = _rebuildCursorMs;
        Pan = _rebuildPan;
        if (_rebuildPlaying)
            Play();
    }

    public void Dispose()
    {
        if (IsDisposed)
            return;
        IsDisposed = true;
        UnloadCurrent();
        if (_sound != IntPtr.Zero)
        {
            NativeMethods.ma_sound_free(_sound);
            _sound = IntPtr.Zero;
        }
    }
}
