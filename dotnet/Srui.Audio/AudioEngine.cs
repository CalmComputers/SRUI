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

    public AudioEngine()
    {
        Handle = NativeMethods.ma_engine_alloc();
        if (Handle == IntPtr.Zero)
            throw new AudioException("audio engine allocation failed");

        if (NativeMethods.ma_engine_init_with_caching(Handle) != 0)
        {
            NativeMethods.ma_engine_free(Handle);
            Handle = IntPtr.Zero;
            throw new AudioException("audio engine initialization failed");
        }

        // Steam Audio for HRTF; 512 must match the engine's period size.
        var sampleRate = NativeMethods.ma_engine_get_sample_rate(Handle);
        HrtfAvailable = NativeMethods.ma_phonon_init(sampleRate, 512) == 0
            || NativeMethods.ma_phonon_is_initialized() != 0;
    }

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

    public void Dispose()
    {
        if (Handle == IntPtr.Zero)
            return;
        if (HrtfAvailable)
            NativeMethods.ma_phonon_uninit();
        NativeMethods.ma_engine_uninit_with_caching(Handle);
        NativeMethods.ma_engine_free(Handle);
        Handle = IntPtr.Zero;
    }
}
