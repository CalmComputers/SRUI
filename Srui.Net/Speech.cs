using Srui.Core;

namespace Srui;

/// <summary>Speech output through Prism — the running screen reader if
/// there is one, platform TTS otherwise. Owns a Prism context and one
/// backend instance; create it on the thread that speaks.</summary>
public sealed class Speech : IDisposable
{
    private IntPtr _ctx;
    private IntPtr _backend;

    public Speech()
    {
        var cfg = PrismNative.prism_config_init();
        _ctx = PrismNative.prism_init(ref cfg);
        if (_ctx == IntPtr.Zero)
            throw new InvalidOperationException("prism initialization failed");
        _backend = PrismNative.prism_registry_create_best(_ctx);
        if (_backend == IntPtr.Zero)
        {
            PrismNative.prism_shutdown(_ctx);
            _ctx = IntPtr.Zero;
            throw new InvalidOperationException("no speech backend available");
        }
        var err = PrismNative.prism_backend_initialize(_backend);
        if (err != PrismNative.Ok && err != PrismNative.ErrorAlreadyInitialized)
        {
            PrismNative.prism_backend_free(_backend);
            PrismNative.prism_shutdown(_ctx);
            _backend = IntPtr.Zero;
            _ctx = IntPtr.Zero;
            throw new InvalidOperationException($"speech backend initialization failed ({err})");
        }
    }

    public void Dispose()
    {
        if (_backend != IntPtr.Zero)
        {
            PrismNative.prism_backend_free(_backend);
            _backend = IntPtr.Zero;
        }
        if (_ctx != IntPtr.Zero)
        {
            PrismNative.prism_shutdown(_ctx);
            _ctx = IntPtr.Zero;
        }
    }

    /// <summary>Name of the active backend (e.g. "NVDA", "SAPI").</summary>
    public string BackendName
    {
        get
        {
            var ptr = PrismNative.prism_backend_name(_backend);
            return ptr == IntPtr.Zero
                ? ""
                : System.Runtime.InteropServices.Marshal.PtrToStringUTF8(ptr) ?? "";
        }
    }

    /// <summary>Speak; interrupt cuts off the current utterance. NUL
    /// bytes would truncate at the boundary and are replaced.</summary>
    public bool Speak(string text, bool interrupt = false) =>
        PrismNative.prism_backend_speak(_backend, text.Replace('\0', ' '), interrupt)
            == PrismNative.Ok;

    /// <summary>Silence the current utterance and drop queued ones.
    /// Stopping when nothing is speaking is not a fault.</summary>
    public void Stop() => PrismNative.prism_backend_stop(_backend);

    /// <summary>Whether the backend reports speech in progress.</summary>
    public bool IsSpeaking =>
        PrismNative.prism_backend_is_speaking(_backend, out var speaking) == PrismNative.Ok
        && speaking;
}

/// <summary>The reference self-voicing reader: renders accessibility
/// events to utterances with <see cref="SpeechRenderer"/> and forwards
/// them to a Prism voice. Installed out of the box by a windowed
/// <see cref="SruiApp"/>; interruption policy (silencing on a keypress)
/// lives here, not in the engine.</summary>
public sealed class SpeechReader : IReader, IDisposable
{
    public Speech Voice { get; }

    /// <summary>A reader over the best available Prism backend.</summary>
    public SpeechReader() : this(new Speech())
    {
    }

    /// <summary>A reader over an existing voice; the reader takes
    /// ownership and disposes it.</summary>
    public SpeechReader(Speech voice) => Voice = voice;

    public void OnEvent(AccessibilityEvent e)
    {
        // An event that renders to silence is skipped.
        if (SpeechRenderer.RenderEvent(e) is string text)
            Voice.Speak(text);
    }

    public void OnInterrupt() => Voice.Stop();

    public void Dispose() => Voice.Dispose();
}
