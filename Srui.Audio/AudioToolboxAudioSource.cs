namespace Srui.Audio;

/// <summary>
/// AAC / M4A / M4B / ALAC decoding through macOS AudioToolbox's
/// ExtAudioFile, surfaced as a <see cref="PullAudioSource"/> — the macOS
/// counterpart of <see cref="MediaFoundationAudioSource"/>, for the formats
/// cosmos has no native backend for. Streaming: frames are decoded to
/// interleaved f32 at the file's own rate and channel count as the engine
/// asks, never decoded whole. The decoder is the macOS build of cosmos
/// (cosmos_apple_audio.c), so it exists on macOS only;
/// <see cref="SystemAudioSource"/> picks this or the Media Foundation
/// reader by platform.
/// </summary>
public sealed unsafe class AudioToolboxAudioSource : PullAudioSource
{
    private readonly object _lock = new();
    private IntPtr _file;

    /// <summary>Total duration, net of the encoder's priming frames.</summary>
    public ulong DurationMs { get; }

    private AudioToolboxAudioSource(
        IntPtr file, uint channels, uint sampleRate, ulong lengthFrames, ulong durationMs)
        : base(channels, sampleRate, lengthFrames)
    {
        _file = file;
        DurationMs = durationMs;
    }

    /// <summary>Open a file for decoding. Throws AudioException when
    /// AudioToolbox cannot read it, and PlatformNotSupportedException
    /// off macOS.</summary>
    public static AudioToolboxAudioSource Open(string path)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException(
                "AudioToolboxAudioSource decodes through macOS AudioToolbox; use SystemAudioSource.Open for the platform's decoder.");
        var file = NativeMethods.cosmos_atx_open(
            path, out var channels, out var sampleRate, out var lengthFrames, out var durationMs);
        if (file == IntPtr.Zero)
            throw new AudioException($"AudioToolbox could not open '{path}'");
        return new AudioToolboxAudioSource(file, channels, sampleRate, lengthFrames, durationMs);
    }

    /// <summary>Duration-only probe: open, read the length, close. 0 when
    /// the file is unreadable — the managed complement of
    /// <see cref="Sound.ProbeDurationMs"/>.</summary>
    public static ulong ProbeDurationMs(string path)
    {
        try
        {
            using var source = Open(path);
            return source.DurationMs;
        }
        catch (AudioException)
        {
            return 0;
        }
    }

    public override ulong Read(Span<float> buffer, ulong frameCount)
    {
        lock (_lock)
        {
            if (_file == IntPtr.Zero)
                return 0;
            var fits = (ulong)buffer.Length / Channels;
            if (frameCount > fits)
                frameCount = fits;
            fixed (float* output = buffer)
                return NativeMethods.cosmos_atx_read(_file, output, frameCount);
        }
    }

    public override bool Seek(ulong frameIndex)
    {
        lock (_lock)
        {
            return _file != IntPtr.Zero && NativeMethods.cosmos_atx_seek(_file, frameIndex) == 0;
        }
    }

    public override void Dispose()
    {
        lock (_lock)
        {
            if (_file != IntPtr.Zero)
            {
                NativeMethods.cosmos_atx_close(_file);
                _file = IntPtr.Zero;
            }
        }
        base.Dispose();
    }
}
