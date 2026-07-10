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
    internal AudioEngine Engine { get; }
    internal BinauralPool BinauralPool { get; }

    private readonly List<WeakReference<Sound>> _sounds = new();
    private readonly List<WeakReference<SoundGroup>> _groups = new();

    public SoundManager()
    {
        Engine = new AudioEngine();
        BinauralPool = new BinauralPool(Engine);
    }

    /// <summary>Live HRTF convolvers. Sounds sharing a position (and bus)
    /// share one, so this is usually far below the HRTF sound count.</summary>
    public int ActiveHrtfConvolvers => BinauralPool.ActiveConvolvers;

    public void Dispose()
    {
        BinauralPool.Clear();
        Engine.Dispose();
    }

    /// <summary>Create a sound routed to the engine endpoint (or through
    /// `group` when given). Load it, position it, play it.</summary>
    public Sound CreateSound(SoundGroup? group = null)
    {
        var sound = new Sound(this, group);
        _sounds.Add(new WeakReference<Sound>(sound));
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

    /// <summary>Advance per-sound and per-group automation and refresh
    /// spatialization. Equivalent to nudging the listener.</summary>
    public void Tick()
    {
        TickGroups();
        UpdateAllSounds();
    }

    public void SetListenerPosition(float x, float y, float z)
    {
        Engine.SetListenerPosition(x, y, z);
        UpdateAllSounds();
    }

    /// <summary>Degrees, unit circle: 0 = +X (east), 90 = +Y (north/forward).</summary>
    public void SetListenerAngle(float degrees)
    {
        Engine.SetListenerAngle(degrees);
        UpdateAllSounds();
    }

    /// <summary>Set position and facing angle together.</summary>
    public void SetListener(float x, float y, float z, float angleDegrees)
    {
        Engine.SetListenerPosition(x, y, z);
        Engine.SetListenerAngle(angleDegrees);
        UpdateAllSounds();
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

    private void UpdateAllSounds()
    {
        // Swap-remove dead references in place; no allocation.
        for (var i = _sounds.Count - 1; i >= 0; i--)
        {
            if (_sounds[i].TryGetTarget(out var sound) && !sound.IsDisposed)
                sound.UpdateSpatialization();
            else
            {
                _sounds[i] = _sounds[^1];
                _sounds.RemoveAt(_sounds.Count - 1);
            }
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
