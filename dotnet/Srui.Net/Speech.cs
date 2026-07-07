namespace Srui;

/// <summary>Speech output through Prism — the running screen reader if
/// there is one, platform TTS otherwise.</summary>
public sealed class Speech : IDisposable
{
    private IntPtr _handle;

    public Speech()
    {
        _handle = NativeMethods.srui_speech_new();
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("no speech backend available");
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            NativeMethods.srui_speech_free(_handle);
            _handle = IntPtr.Zero;
        }
    }

    public string BackendName =>
        NativeMethods.TakeString(NativeMethods.srui_speech_backend_name(_handle)) ?? "";

    /// <summary>Speak; interrupt cuts off the current utterance.</summary>
    public bool Speak(string text, bool interrupt = false) =>
        NativeMethods.srui_speech_speak(_handle, text, interrupt);

    /// <summary>Silence the current utterance and drop queued ones.</summary>
    public void Stop() => NativeMethods.srui_speech_stop(_handle);
}
