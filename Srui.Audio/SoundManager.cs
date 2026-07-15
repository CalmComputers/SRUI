namespace Srui.Audio;

/// <summary>
/// Owns the audio engine, creates sounds and groups, and drives per-frame
/// updates. Everything is edge-triggered except time-based automation
/// (pitch tweens): call <see cref="Tick"/> once per game frame to apply
/// it. Apps built on SruiApp never do this themselves — SruiApp.Audio is
/// an app-owned manager ticked from the event loop. Not thread-safe: one
/// manager, one thread. Steady-state Tick/SetListener allocate nothing.
///
/// The listener starts at the origin facing +X (angle 0, "east"); games
/// that treat +Y as forward should call
/// <c>SetListener(x, y, z, 90)</c> once up front.
/// </summary>
public sealed class SoundManager : IDisposable
{
    internal AudioEngine Engine { get; private set; }

    private readonly List<WeakReference<Sound>> _sounds = new();
    private readonly List<WeakReference<SoundGroup>> _groups = new();
    private readonly List<WeakReference<SoundEntity>> _entities = new();
    private readonly List<Sound> _oneshots = new();

    /// <param name="periodFrames">Requested device period in frames; 0
    /// selects the 128-frame default (~2.7ms at 48kHz — lowest
    /// trigger-to-ear latency). Heavy scenes (hundreds of voices, many
    /// HRTF positions) should request more headroom, e.g. 512. The
    /// device may align or clamp the request; read
    /// <see cref="DevicePeriodFrames"/> for the grant. The period is
    /// fixed for the manager's lifetime — the device and the HRTF
    /// convolvers are built around it — so changing it means creating
    /// a new manager.</param>
    public SoundManager(uint periodFrames = 0)
    {
        Engine = new AudioEngine(periodFrames);
    }

    /// <summary>Live HRTF convolvers — one per HRTF-enabled entity.</summary>
    public int ActiveHrtfConvolvers
    {
        get
        {
            var count = 0;
            foreach (var weak in _entities)
                if (weak.TryGetTarget(out var entity) && !entity.IsDisposed && entity.HasConvolver)
                    count++;
            return count;
        }
    }

    /// <summary>Rebuild the engine at a new device period, preserving
    /// the soundscape: every live Sound and SoundGroup keeps its object
    /// and state — source, position, effect chain, transport, playback
    /// cursor, running tweens — replayed against the new device, and
    /// the decode cache survives so nothing re-decodes. Output gaps
    /// briefly while the device restarts; call at silence-tolerant
    /// moments (startup, menus, an options apply). A sound whose source
    /// file no longer loads is left unloaded; the rest proceed. The
    /// device may align or clamp the request — read
    /// <see cref="DevicePeriodFrames"/> for the grant; 0 selects the
    /// 128-frame default.</summary>
    public void Reconfigure(uint periodFrames)
    {
        var listenerX = Engine.ListenerX;
        var listenerY = Engine.ListenerY;
        var listenerZ = Engine.ListenerZ;
        var listenerAngle = Engine.ListenerAngle;

        // Sounds release first (they detach from groups), then the
        // entities' convolvers, then groups children-before-parents.
        foreach (var weak in _sounds)
            if (weak.TryGetTarget(out var sound) && !sound.IsDisposed)
                sound.ReleaseNative();
        foreach (var weak in _entities)
            if (weak.TryGetTarget(out var entity) && !entity.IsDisposed)
                entity.ReleaseNative();
        for (var i = _groups.Count - 1; i >= 0; i--)
            if (_groups[i].TryGetTarget(out var group) && !group.IsDisposed)
                group.ReleaseNative();

        Engine.DisposeKeepCache();
        Engine = new AudioEngine(periodFrames);
        Engine.SetListenerPosition(listenerX, listenerY, listenerZ);
        Engine.SetListenerAngle(listenerAngle);

        // Rebuild parents-before-children (creation order), then the
        // entities' convolvers onto the fresh groups, then sounds.
        foreach (var weak in _groups)
            if (weak.TryGetTarget(out var group) && !group.IsDisposed)
                group.RecreateNative();
        foreach (var weak in _entities)
            if (weak.TryGetTarget(out var entity) && !entity.IsDisposed)
                entity.RecreateNative();
        foreach (var weak in _sounds)
            if (weak.TryGetTarget(out var sound) && !sound.IsDisposed)
                sound.RecreateNative();
    }

    /// <summary>Dispose every live sound and group this manager created,
    /// then the engine itself. Sounds and groups disposed here become
    /// disposed objects exactly as if the caller had disposed them, so
    /// teardown order is never the caller's problem.</summary>
    public void Dispose()
    {
        foreach (var weak in _sounds)
            if (weak.TryGetTarget(out var sound))
                sound.Dispose();
        _sounds.Clear();
        _oneshots.Clear();
        foreach (var weak in _entities)
            if (weak.TryGetTarget(out var entity))
                entity.Dispose();
        _entities.Clear();
        foreach (var weak in _groups)
            if (weak.TryGetTarget(out var group))
                group.Dispose();
        _groups.Clear();
        Engine.Dispose();
    }

    /// <summary>Create a sound routed to the engine endpoint (or through
    /// `group` when given). Load it, position it, play it.
    ///
    /// A oneshot sound is owned by the manager: the caller may drop the
    /// returned reference immediately, and the manager disposes the sound
    /// on the first <see cref="Tick"/> after it finishes playing (fire and
    /// forget). Don't make a oneshot loop or pause it indefinitely — a
    /// oneshot that never reaches its end (or is never played) is held
    /// until the caller disposes it or the manager is disposed.</summary>
    public Sound CreateSound(SoundGroup? group = null, bool oneshot = false)
    {
        var sound = new Sound(this, group);
        _sounds.Add(new WeakReference<Sound>(sound));
        if (oneshot)
            _oneshots.Add(sound);
        return sound;
    }

    /// <summary>Create a bus. Pass a parent to nest buses; null attaches
    /// to the engine endpoint.</summary>
    public SoundGroup CreateGroup(SoundGroup? parent = null)
    {
        var group = new SoundGroup(this, parent);
        _groups.Add(new WeakReference<SoundGroup>(group));
        return group;
    }

    /// <summary>Create a physical sound source. Its sounds are made with
    /// <see cref="CreateSound"/> into <see cref="SoundEntity.Group"/>;
    /// the entity spatializes them together as one source. Pass a parent
    /// to route the entity through a bus (a category, a master).</summary>
    public SoundEntity CreateEntity(SoundGroup? parent = null)
    {
        var entity = new SoundEntity(this, parent);
        _entities.Add(new WeakReference<SoundEntity>(entity));
        return entity;
    }

    /// <summary>Advance per-sound and per-group automation, apply
    /// entity spatialization, and reap finished oneshot sounds.</summary>
    public void Tick()
    {
        TickGroups();
        TickSounds();
        UpdateEntities();
        ReapOneshots();
    }

    // Listener changes mark every entity stale rather than sweeping
    // immediately: a frame's worth of entity and listener movement
    // collapses into the single apply pass in Tick.
    private bool _listenerDirty;

    public void SetListenerPosition(float x, float y, float z)
    {
        Engine.SetListenerPosition(x, y, z);
        _listenerDirty = true;
    }

    /// <summary>Degrees, unit circle: 0 = +X (east), 90 = +Y (north/forward).</summary>
    public void SetListenerAngle(float degrees)
    {
        Engine.SetListenerAngle(degrees);
        _listenerDirty = true;
    }

    /// <summary>Set position and facing angle together.</summary>
    public void SetListener(float x, float y, float z, float angleDegrees)
    {
        Engine.SetListenerPosition(x, y, z);
        Engine.SetListenerAngle(angleDegrees);
        _listenerDirty = true;
    }

    public float ListenerX => Engine.ListenerX;
    public float ListenerY => Engine.ListenerY;
    public float ListenerZ => Engine.ListenerZ;
    public float ListenerAngle => Engine.ListenerAngle;

    /// <summary>Whether Steam Audio HRTF is available.</summary>
    public bool IsHrtfAvailable => Engine.HrtfAvailable;

    public uint SampleRate => Engine.SampleRate;

    /// <summary>The device period in frames — the granularity at which
    /// the device pulls audio, and the dominant term in trigger-to-ear
    /// latency. Divide by <see cref="SampleRate"/> for seconds.</summary>
    public uint DevicePeriodFrames => Engine.PeriodFrames;

    /// <summary>Read the device-callback timing counters, optionally
    /// resetting them. A callback that takes longer than its budget
    /// (period frames / sample rate) is a buffer underrun — an audible
    /// gap — so <see cref="CallbackStats.Overruns"/> distinguishes real
    /// underruns from other click sources. Counters are process-wide
    /// (they span engine rebuilds) and reads race the audio thread
    /// benignly.</summary>
    public CallbackStats GetCallbackStats(bool reset = false)
    {
        NativeMethods.ma_engine_get_callback_stats(
            out var callbacks, out var overruns, out var maxNs, out var budgetNs,
            reset ? 1u : 0u);
        return new CallbackStats(callbacks, overruns, maxNs, budgetNs);
    }

    private void TickSounds()
    {
        // Swap-remove dead references in place; no allocation.
        for (var i = _sounds.Count - 1; i >= 0; i--)
        {
            if (_sounds[i].TryGetTarget(out var sound) && !sound.IsDisposed)
                sound.AdvancePitchTween();
            else
            {
                _sounds[i] = _sounds[^1];
                _sounds.RemoveAt(_sounds.Count - 1);
            }
        }
    }

    private void UpdateEntities()
    {
        // One apply pass per Tick: everything when the listener moved,
        // otherwise only entities that themselves changed.
        var all = _listenerDirty;
        _listenerDirty = false;

        for (var i = _entities.Count - 1; i >= 0; i--)
        {
            if (_entities[i].TryGetTarget(out var entity) && !entity.IsDisposed)
            {
                if (all || entity.NeedsSpatialUpdate)
                    entity.UpdateSpatialization();
            }
            else
            {
                _entities[i] = _entities[^1];
                _entities.RemoveAt(_entities.Count - 1);
            }
        }
    }

    private void ReapOneshots()
    {
        for (var i = _oneshots.Count - 1; i >= 0; i--)
        {
            var sound = _oneshots[i];
            if (!sound.IsDisposed && !sound.AtEnd)
                continue;
            sound.Dispose();
            _oneshots[i] = _oneshots[^1];
            _oneshots.RemoveAt(_oneshots.Count - 1);
        }
    }

    private void TickGroups()
    {
        for (var i = _groups.Count - 1; i >= 0; i--)
        {
            if (_groups[i].TryGetTarget(out var group) && !group.IsDisposed)
                group.AdvancePitchTween();
            else
            {
                _groups[i] = _groups[^1];
                _groups.RemoveAt(_groups.Count - 1);
            }
        }
    }
}
