namespace Srui.Audio;

/// <summary>The miniaudio engine + listener state. Owned by SoundManager.</summary>
internal sealed class AudioEngine : IDisposable
{
    internal IntPtr Handle { get; private set; }

    public bool HrtfAvailable { get; }

    public float ListenerX { get; private set; }
    public float ListenerY { get; private set; }
    public float ListenerZ { get; private set; }

    /// <summary>Degrees, unit circle: 0 = +X (east), 90 = +Y (north/forward).</summary>
    public float ListenerAngle { get; private set; }

    /// <param name="periodFrames">Requested device period in frames;
    /// 0 selects the 128-frame default. The device may align or clamp
    /// the request — read <see cref="PeriodFrames"/> for the grant.</param>
    public AudioEngine(uint periodFrames = 0)
    {
        Handle = NativeMethods.ma_engine_alloc();
        if (Handle == IntPtr.Zero)
            throw new AudioException("audio engine allocation failed");

        if (NativeMethods.ma_engine_init_with_caching(Handle, periodFrames) != 0)
        {
            NativeMethods.ma_engine_free(Handle);
            Handle = IntPtr.Zero;
            throw new AudioException("audio engine initialization failed");
        }

        // Steam Audio's frame size must match the device period — so ask
        // the engine what WASAPI actually granted (the request may be
        // aligned or clamped by the driver) and size phonon to that.
        var sampleRate = NativeMethods.ma_engine_get_sample_rate(Handle);
        PeriodFrames = NativeMethods.ma_engine_get_actual_period_frames(Handle);
        if (PeriodFrames == 0)
            PeriodFrames = 256;
        HrtfAvailable = NativeMethods.ma_phonon_init(sampleRate, PeriodFrames) == 0
            || NativeMethods.ma_phonon_is_initialized() != 0;
    }

    /// <summary>The device period in frames — the granularity at which
    /// the device pulls audio, and the dominant term in trigger-to-ear
    /// latency (one period of start jitter plus the buffer depth).</summary>
    public uint PeriodFrames { get; }

    public uint SampleRate =>
        Handle == IntPtr.Zero ? 44100 : NativeMethods.ma_engine_get_sample_rate(Handle);

    public IntPtr Endpoint => NativeMethods.ma_engine_get_endpoint(Handle);

    public IntPtr NodeGraph => NativeMethods.ma_engine_get_node_graph(Handle);

    public void SetListenerPosition(float x, float y, float z)
    {
        ListenerX = x;
        ListenerY = y;
        ListenerZ = z;
        NativeMethods.ma_engine_listener_set_position(Handle, 0, x, y, z);
    }

    public void SetListenerAngle(float degrees)
    {
        // miniaudio uses direction vectors; the angle feeds our own
        // spatialization math instead.
        ListenerAngle = degrees;
    }

    public void Dispose() => DisposeCore(keepCache: false);

    /// <summary>Dispose but leave the process-wide decode cache alive
    /// for an engine about to be built in its place (a period change),
    /// so reloads skip re-decoding.</summary>
    internal void DisposeKeepCache() => DisposeCore(keepCache: true);

    private void DisposeCore(bool keepCache)
    {
        if (Handle == IntPtr.Zero)
            return;
        if (HrtfAvailable)
            NativeMethods.ma_phonon_uninit();
        NativeMethods.ma_engine_uninit_with_caching(Handle, keepCache ? 1u : 0u);
        NativeMethods.ma_engine_free(Handle);
        Handle = IntPtr.Zero;
    }
}
