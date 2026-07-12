using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Srui.Audio;

/// <summary>
/// A managed decoder feeding the engine: subclass, produce interleaved
/// f32 frames in <see cref="Read"/> and honor <see cref="Seek"/>, then
/// hand the source to <see cref="Sound.LoadPull"/> (which takes
/// ownership). Callbacks arrive on whichever thread pulls the sound —
/// the audio thread — so implementations must be self-contained and
/// exception-free (an escaped exception cannot cross the native
/// boundary; the thunks translate any into end-of-stream).
/// </summary>
public abstract class PullAudioSource : IDisposable
{
    private IntPtr _handle;
    private GCHandle _self;

    protected PullAudioSource(uint channels, uint sampleRate, ulong lengthFrames)
    {
        if (channels == 0 || sampleRate == 0)
            throw new ArgumentOutOfRangeException(nameof(channels),
                "channels and sample rate must be nonzero");
        Channels = channels;
        SampleRate = sampleRate;
        LengthFrames = lengthFrames;
    }

    public uint Channels { get; }
    public uint SampleRate { get; }

    /// <summary>Total frames, or 0 when unknown (length and seeking
    /// beyond what was read may then be unavailable to the engine).</summary>
    public ulong LengthFrames { get; }

    /// <summary>Fill the buffer with up to frameCount interleaved f32
    /// frames; return the frames produced. 0 means end of stream.</summary>
    protected abstract ulong Read(Span<float> buffer, ulong frameCount);

    /// <summary>Reposition to the absolute frame. False refuses the seek.</summary>
    protected abstract bool Seek(ulong frameIndex);

    internal unsafe IntPtr Handle
    {
        get
        {
            if (_handle == IntPtr.Zero)
            {
                _self = GCHandle.Alloc(this);
                _handle = NativeMethods.cosmos_pull_ds_create(
                    Channels, SampleRate, LengthFrames,
                    &ReadThunk, &SeekThunk, GCHandle.ToIntPtr(_self));
                if (_handle == IntPtr.Zero)
                {
                    _self.Free();
                    throw new AudioException("failed to create pull data source");
                }
            }
            return _handle;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe ulong ReadThunk(IntPtr user, float* frames, ulong frameCount)
    {
        try
        {
            var source = (PullAudioSource)GCHandle.FromIntPtr(user).Target!;
            var samples = checked((int)(frameCount * source.Channels));
            return source.Read(new Span<float>(frames, samples), frameCount);
        }
        catch
        {
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint SeekThunk(IntPtr user, ulong frameIndex)
    {
        try
        {
            var source = (PullAudioSource)GCHandle.FromIntPtr(user).Target!;
            return source.Seek(frameIndex) ? 1u : 0u;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Release the native source. Only after the consuming
    /// sound is uninitialized — <see cref="Sound"/> owns that ordering.</summary>
    public virtual void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            NativeMethods.cosmos_pull_ds_destroy(_handle);
            _handle = IntPtr.Zero;
            _self.Free();
        }
        GC.SuppressFinalize(this);
    }
}
