using System.Runtime.InteropServices;

namespace Srui.Audio;

/// <summary>
/// A playable sound with 3D positioning: basic pan/volume spatialization
/// (Horizon-style) or Steam Audio HRTF. Positions are AABBs — for point
/// sounds min == max. Create via <see cref="SoundManager.CreateSound"/>.
///
/// Coordinate system: X left/right, Y backward/forward, Z down/up.
/// Listener angle in degrees, unit circle (0 = +X east, 90 = +Y forward).
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

    // HRTF binaural node, when enabled and available. Leased from the
    // manager's BinauralPool — sounds at the same position share one.
    private IntPtr _binauralNode;
    private BinauralKey _binauralKey;

    // 3D position as AABB.
    private float _minX, _minY, _minZ, _maxX, _maxY, _maxZ;

    private bool _stationary;
    private bool _hrtfEnabled;

    private float _minDistance = 1.0f;
    private float _maxDistance = 100.0f;
    private float _rolloff = 1.0f;
    private float _minGain;
    private float _maxGain = 1.0f;

    // Horizon-style spatialization parameters.
    private float _panStep = 0.05f;
    private float _volumeStep = 0.0333333f;
    private float _behindPitchDecrease = 0.04f;
    private bool _hardClosePan = true;

    private float _baseVolume = 1.0f;
    private float _basePitch = 1.0f;
    private PitchTween _pitchTween;
    private bool _tweening;

    // What Load*/… was last called with, so an engine rebuild
    // (SoundManager.Reconfigure) can replay it.
    private enum LoadKind : byte { None, Plain, Stretched, Reversed }
    private LoadKind _loadKind;
    private string? _loadPath;
    private float _stretchFactor;

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
    /// the same file is loaded more than once.</summary>
    public void Load(string filename)
    {
        UnloadCurrent();
        var result = NativeMethods.ma_sound_init_from_file(
            Engine.Handle, filename, NativeMethods.SoundFlagDecode, GroupPtr, IntPtr.Zero, _sound);
        if (result != 0)
            throw new AudioException($"failed to load '{filename}'");
        _loadKind = LoadKind.Plain;
        _loadPath = filename;
        FinishLoad();
    }

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
        // We do spatialization ourselves.
        NativeMethods.ma_sound_set_spatialization_enabled(_sound, 0);
        if (_hrtfEnabled && Engine.HrtfAvailable)
            CreateBinauralNode();
    }

    private void UnloadCurrent()
    {
        DestroyBinauralNode();
        if (_loaded)
        {
            NativeMethods.ma_sound_uninit(_sound);
            _loaded = false;
        }
        if (_bufferRef != IntPtr.Zero)
        {
            NativeMethods.cosmos_buffer_ref_destroy(_bufferRef);
            _bufferRef = IntPtr.Zero;
        }
        FreePcm();
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

    /// <summary>Playback position in milliseconds.</summary>
    public ulong PlaybackPosition
    {
        get
        {
            if (!_loaded) return 0;
            if (NativeMethods.ma_sound_get_cursor_in_pcm_frames(_sound, out var cursor) != 0)
                return 0;
            var sr = Engine.SampleRate;
            return sr == 0 ? 0 : cursor * 1000 / sr;
        }
        set
        {
            if (!_loaded) return;
            var sr = Engine.SampleRate;
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
            var sr = Engine.SampleRate;
            return sr == 0 ? 0 : frames * 1000 / sr;
        }
    }

    // ── Position ──

    /// <summary>Point position (zero-size AABB).</summary>
    public void SetPosition(float x, float y, float z)
    {
        _minX = _maxX = x;
        _minY = _maxY = y;
        _minZ = _maxZ = z;
        RefreshBinauralLease();
        UpdateSpatialization();
    }

    /// <summary>Ranged AABB position.</summary>
    public void SetPositionRanged(float minX, float maxX, float minY, float maxY, float minZ, float maxZ)
    {
        _minX = minX; _maxX = maxX;
        _minY = minY; _maxY = maxY;
        _minZ = minZ; _maxZ = maxZ;
        RefreshBinauralLease();
        UpdateSpatialization();
    }

    public float X => _minX;
    public float Y => _minY;
    public float Z => _minZ;
    public float MaxX => _maxX;
    public float MaxY => _maxY;
    public float MaxZ => _maxZ;

    /// <summary>Stationary sounds follow the listener: no spatial effects.</summary>
    public bool Stationary
    {
        get => _stationary;
        set
        {
            _stationary = value;
            RefreshBinauralLease();
            UpdateSpatialization();
        }
    }

    // ── Mix parameters ──

    /// <summary>Base volume before 3D attenuation (0..1).</summary>
    public float BaseVolume
    {
        get => _baseVolume;
        set { _baseVolume = value; UpdateSpatialization(); }
    }

    /// <summary>Base pitch (1 = normal). Setting cancels a running tween.</summary>
    public float Pitch
    {
        get => _basePitch;
        set { _basePitch = value; _tweening = false; UpdateSpatialization(); }
    }

    /// <summary>Interpolate pitch to `target` over `duration`. Advances on
    /// listener updates and <see cref="SoundManager.Tick"/>.</summary>
    public void TweenPitch(float target, TimeSpan duration, Easing easing = Easing.Linear)
    {
        if (_tweening)
            _basePitch = _pitchTween.Sample(out _);
        _pitchTween = PitchTween.Start(_basePitch, target, duration, easing);
        _tweening = true;
        UpdateSpatialization();
    }

    /// <summary>Cancel the tween; pitch holds at its current value.</summary>
    public void StopPitchTween()
    {
        if (_tweening)
            _basePitch = _pitchTween.Sample(out _);
        _tweening = false;
        UpdateSpatialization();
    }

    public bool IsPitchTweening => _tweening;

    /// <summary>Direct pan (-1..1); overridden by 3D positioning.</summary>
    public float Pan
    {
        get => _loaded ? NativeMethods.ma_sound_get_pan(_sound) : 0.0f;
        set
        {
            if (_loaded) NativeMethods.ma_sound_set_pan(_sound, value);
        }
    }

    /// <summary>HRTF via Steam Audio when true; pan/volume otherwise.</summary>
    public bool Hrtf
    {
        get => _hrtfEnabled;
        set
        {
            if (_hrtfEnabled == value) return;
            _hrtfEnabled = value;
            if (value && Engine.HrtfAvailable)
                CreateBinauralNode();
            else
                DestroyBinauralNode();
            UpdateSpatialization();
        }
    }

    public float MinDistance
    {
        get => _minDistance;
        set { _minDistance = value; UpdateSpatialization(); }
    }

    public float MaxDistance
    {
        get => _maxDistance;
        set { _maxDistance = value; UpdateSpatialization(); }
    }

    public float Rolloff
    {
        get => _rolloff;
        set { _rolloff = value; UpdateSpatialization(); }
    }

    public float MinGain
    {
        get => _minGain;
        set { _minGain = value; UpdateSpatialization(); }
    }

    public float MaxGain
    {
        get => _maxGain;
        set { _maxGain = value; UpdateSpatialization(); }
    }

    /// <summary>Pan per unit horizontal distance (default 0.05).</summary>
    public float PanStep
    {
        get => _panStep;
        set { _panStep = value; UpdateSpatialization(); }
    }

    /// <summary>Volume reduction per unit distance (default 0.0333).</summary>
    public float VolumeStep
    {
        get => _volumeStep;
        set { _volumeStep = value; UpdateSpatialization(); }
    }

    /// <summary>Pitch reduction for sounds behind the listener (default 0.04).</summary>
    public float BehindPitchDecrease
    {
        get => _behindPitchDecrease;
        set { _behindPitchDecrease = value; UpdateSpatialization(); }
    }

    /// <summary>Extra pan separation for close sounds (default true).</summary>
    public bool HardClosePan
    {
        get => _hardClosePan;
        set { _hardClosePan = value; UpdateSpatialization(); }
    }

    // ── Spatialization (zero-alloc) ──

    /// <summary>Recompute pan/volume/pitch (and HRTF direction) from the
    /// current listener state. Called automatically on property changes,
    /// listener moves, and ticks.</summary>
    public void UpdateSpatialization()
    {
        if (!_loaded)
            return;

        AdvancePitchTween();

        if (_stationary)
        {
            NativeMethods.ma_sound_set_pan(_sound, 0.0f);
            NativeMethods.ma_sound_set_volume(_sound, _baseVolume);
            NativeMethods.ma_sound_set_pitch(_sound, _basePitch);
            if (_binauralNode != IntPtr.Zero)
                NativeMethods.ma_phonon_binaural_node_set_direction(_binauralNode, 0.0f, 0.0f, -1.0f, 0.0f);
            return;
        }

        if (_hrtfEnabled && _binauralNode != IntPtr.Zero)
            UpdateHrtfSpatialization();
        else
            UpdateBasicSpatialization();
    }

    private void AdvancePitchTween()
    {
        if (!_tweening)
            return;
        _basePitch = _pitchTween.Sample(out var finished);
        if (finished)
            _tweening = false;
    }

    private void ClosestPointOnAabb(out float x, out float y, out float z)
    {
        x = Math.Clamp(Engine.ListenerX, _minX, _maxX);
        y = Math.Clamp(Engine.ListenerY, _minY, _maxY);
        z = Math.Clamp(Engine.ListenerZ, _minZ, _maxZ);
    }

    private void UpdateHrtfSpatialization()
    {
        const float epsilon = 1e-4f;

        ClosestPointOnAabb(out var edgeX, out var edgeY, out var edgeZ);
        var dx = edgeX - Engine.ListenerX;
        var dy = edgeY - Engine.ListenerY;
        var dz = edgeZ - Engine.ListenerZ;

        var distance = MathF.Sqrt(dx * dx + dy * dy + dz * dz);

        // Rotate horizontal direction by listener angle.
        var rotAngle = (-Engine.ListenerAngle + 90.0f) * (MathF.PI / 180.0f);
        var cosRot = MathF.Cos(rotAngle);
        var sinRot = MathF.Sin(rotAngle);
        var rotX = dx * cosRot - dy * sinRot;
        var rotY = dx * sinRot + dy * cosRot;

        // Steam Audio coordinates: X=right, Y=up, -Z=forward.
        var steamX = rotX;
        var steamY = dz;
        var steamZ = -rotY;

        if (distance > 0.0001f)
        {
            steamX /= distance;
            steamY /= distance;
            steamZ /= distance;
        }
        else
        {
            steamX = 0.0f;
            steamY = 0.0f;
            steamZ = -1.0f;
        }

        NativeMethods.ma_phonon_binaural_node_set_direction(_binauralNode, steamX, steamY, steamZ, distance);

        // Distance attenuation.
        var attenuatedDistance = MathF.Max(distance - _minDistance, 0.0f);
        var volume = attenuatedDistance <= _maxDistance - _minDistance
            ? Volume.DbToLinear(-attenuatedDistance * _rolloff * 1.75f)
            : 0.0f;
        volume = Math.Clamp(volume, _minGain, _maxGain) * _baseVolume;
        NativeMethods.ma_sound_set_volume(_sound, volume);

        // HRTF handles panning.
        NativeMethods.ma_sound_set_pan(_sound, 0.0f);

        // Behind pitch decrease.
        var pitch = _basePitch;
        if (distance > 0.7071f + epsilon)
        {
            if (rotY < -epsilon) pitch -= _behindPitchDecrease;
            if (dz < -epsilon) pitch -= _behindPitchDecrease;
        }
        NativeMethods.ma_sound_set_pitch(_sound, pitch);
    }

    private void UpdateBasicSpatialization()
    {
        const float epsilon = 1e-4f;

        ClosestPointOnAabb(out var edgeX, out var edgeY, out var edgeZ);
        var dx = edgeX - Engine.ListenerX;
        var dy = edgeY - Engine.ListenerY;
        var dz = edgeZ - Engine.ListenerZ;

        var distance = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        var horizontalDistance = MathF.Sqrt(dx * dx + dy * dy);

        var rotAngle = (-Engine.ListenerAngle + 90.0f) * (MathF.PI / 180.0f);
        var cosRot = MathF.Cos(rotAngle);
        var sinRot = MathF.Sin(rotAngle);
        var rotX = dx * cosRot - dy * sinRot;
        var rotY = dx * sinRot + dy * cosRot;

        // Angle-based pan.
        var pan = 0.0f;
        if (horizontalDistance > epsilon)
        {
            var azimuth = MathF.Atan2(rotY, rotX);
            var angleFactor = MathF.Cos(azimuth);
            var distanceFactor = horizontalDistance * _panStep;
            if (_hardClosePan && distance > epsilon)
                distanceFactor += 0.2f;
            pan = Math.Clamp(angleFactor * Math.Clamp(distanceFactor, -1.0f, 1.0f), -1.0f, 1.0f);
        }
        NativeMethods.ma_sound_set_pan(_sound, pan);

        var volume = Math.Clamp(1.0f - distance * _volumeStep, _minGain, _maxGain) * _baseVolume;
        NativeMethods.ma_sound_set_volume(_sound, volume);

        var pitch = _basePitch;
        if (distance > 0.7071f + epsilon)
        {
            if (rotY < -epsilon) pitch -= _behindPitchDecrease;
            if (dz < -epsilon) pitch -= _behindPitchDecrease;
        }
        NativeMethods.ma_sound_set_pitch(_sound, pitch);
    }

    // ── HRTF node wiring (pooled) ──

    private IntPtr DownstreamNodePtr =>
        _group?.NodePtr ?? Engine.Endpoint;

    /// <summary>All the identity a convolver share depends on: the
    /// source AABB (same box → same direction from any listener), the
    /// stationary flag, and the output target. Stationary sounds ignore
    /// position, so they normalize to one key per bus.</summary>
    private BinauralKey CurrentBinauralKey() =>
        _stationary
            ? new BinauralKey(0, 0, 0, 0, 0, 0, true, DownstreamNodePtr)
            : new BinauralKey(_minX, _minY, _minZ, _maxX, _maxY, _maxZ, false, DownstreamNodePtr);

    private void CreateBinauralNode()
    {
        if (_binauralNode != IntPtr.Zero || !Engine.HrtfAvailable || !_loaded)
            return;

        var key = CurrentBinauralKey();
        var node = _manager.BinauralPool.Acquire(key, out var created);
        if (node == IntPtr.Zero)
            return; // continue without HRTF

        if (created)
            NativeMethods.ma_node_attach_output_bus(node, 0, DownstreamNodePtr, 0);

        var soundNode = NativeMethods.ma_sound_get_node_ptr(_sound);
        if (soundNode != IntPtr.Zero)
        {
            NativeMethods.ma_node_detach_output_bus(soundNode, 0);
            NativeMethods.ma_node_attach_output_bus(soundNode, 0, node, 0);
        }
        _binauralNode = node;
        _binauralKey = key;
    }

    /// <summary>Position/stationary changed while pooled: move to the
    /// convolver for the new key. Sole occupants keep their node (the
    /// pool rekeys in place), so moving sources don't churn Steam Audio
    /// effects; joins and splits rewire.</summary>
    private void RefreshBinauralLease()
    {
        if (_binauralNode == IntPtr.Zero)
            return;

        var newKey = CurrentBinauralKey();
        if (newKey == _binauralKey)
            return;

        var node = _manager.BinauralPool.Move(_binauralKey, newKey, out var created, out var releaseOld);
        if (node == IntPtr.Zero)
            return; // keep the current lease on creation failure

        if (created)
            NativeMethods.ma_node_attach_output_bus(node, 0, DownstreamNodePtr, 0);

        if (node != _binauralNode)
        {
            var soundNode = NativeMethods.ma_sound_get_node_ptr(_sound);
            if (soundNode != IntPtr.Zero)
            {
                NativeMethods.ma_node_detach_output_bus(soundNode, 0);
                NativeMethods.ma_node_attach_output_bus(soundNode, 0, node, 0);
            }
        }
        if (releaseOld)
            _manager.BinauralPool.Release(_binauralKey);

        _binauralNode = node;
        _binauralKey = newKey;
    }

    private void DestroyBinauralNode()
    {
        if (_binauralNode == IntPtr.Zero)
            return;
        if (_loaded)
        {
            var soundNode = NativeMethods.ma_sound_get_node_ptr(_sound);
            if (soundNode != IntPtr.Zero)
            {
                NativeMethods.ma_node_detach_output_bus(soundNode, 0);
                NativeMethods.ma_node_attach_output_bus(soundNode, 0, DownstreamNodePtr, 0);
            }
        }
        _manager.BinauralPool.Release(_binauralKey);
        _binauralNode = IntPtr.Zero;
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
    /// position, HRTF, mix parameters, tweens — survives in place.</summary>
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
                    Load(_loadPath!);
                    break;
                case LoadKind.Stretched:
                    LoadStretched(_loadPath!, _stretchFactor);
                    break;
                case LoadKind.Reversed:
                    LoadReversed(_loadPath!);
                    break;
            }
        }
        catch (AudioException)
        {
            return;
        }
        Looping = _rebuildLooping;
        PlaybackPosition = _rebuildCursorMs;
        Pan = _rebuildPan;
        UpdateSpatialization();
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
