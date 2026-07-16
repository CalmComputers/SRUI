namespace Srui.Audio;

/// <summary>
/// A push-fed live source: one producer thread writes interleaved f32
/// samples with <see cref="Write"/>, the audio thread drains them
/// through <see cref="PullAudioSource.Read"/>. Underruns render as
/// silence — the sound stays alive through network hiccups — until the
/// producer calls <see cref="Complete"/>, after which the remaining
/// buffered audio plays out and the stream ends. Built for network
/// audio (streamed generation, voice chat): a single-producer /
/// single-consumer ring with no locks and no allocation on either side
/// after construction.
/// </summary>
public sealed class PushAudioSource : PullAudioSource
{
    private readonly float[] _ring;
    // Monotonic sample counts; the ring index is count % capacity.
    // Written only by their own side, read by both via Volatile.
    private long _written;
    private long _read;
    private volatile bool _completed;

    /// <param name="capacityFrames">Ring size. Sizing it is a latency
    /// choice: a full ring makes <see cref="Write"/> return short, so
    /// the producer's backlog — not this buffer — holds the excess.</param>
    public PushAudioSource(uint channels, uint sampleRate, uint capacityFrames)
        : base(channels, sampleRate, 0)
    {
        if (capacityFrames == 0)
            throw new ArgumentOutOfRangeException(nameof(capacityFrames));
        _ring = new float[checked((int)(capacityFrames * channels))];
    }

    public uint CapacityFrames => (uint)(_ring.Length / Channels);

    /// <summary>Whole frames currently buffered and unread.</summary>
    public uint BufferedFrames =>
        (uint)((Volatile.Read(ref _written) - Volatile.Read(ref _read)) / Channels);

    /// <summary>True once <see cref="Complete"/> has been called.</summary>
    public bool IsCompleted => _completed;

    /// <summary>Append samples (interleaved f32, any granularity — a
    /// frame split across two writes becomes readable when its last
    /// sample lands). Returns how many samples were stored; short when
    /// the ring is full. Producer thread only.</summary>
    public int Write(ReadOnlySpan<float> samples)
    {
        long written = _written;
        long free = _ring.Length - (written - Volatile.Read(ref _read));
        int count = (int)Math.Min(samples.Length, free);
        if (count <= 0)
            return 0;

        int index = (int)(written % _ring.Length);
        int first = Math.Min(count, _ring.Length - index);
        samples[..first].CopyTo(_ring.AsSpan(index));
        samples[first..count].CopyTo(_ring);
        Volatile.Write(ref _written, written + count);
        return count;
    }

    /// <summary>Mark the end of the stream: buffered audio plays out,
    /// then <see cref="PullAudioSource.Read"/> reports end-of-stream
    /// instead of silence. Irreversible.</summary>
    public void Complete() => _completed = true;

    public override ulong Read(Span<float> buffer, ulong frameCount)
    {
        long read = _read;
        long availableSamples = Volatile.Read(ref _written) - read;
        long availableFrames = availableSamples / Channels;
        long takeFrames = Math.Min((long)frameCount, availableFrames);
        int takeSamples = (int)(takeFrames * Channels);

        if (takeSamples > 0)
        {
            int index = (int)(read % _ring.Length);
            int first = Math.Min(takeSamples, _ring.Length - index);
            _ring.AsSpan(index, first).CopyTo(buffer);
            _ring.AsSpan(0, takeSamples - first).CopyTo(buffer[first..]);
            Volatile.Write(ref _read, read + takeSamples);
        }

        if ((ulong)takeFrames == frameCount)
            return frameCount;
        if (_completed)
            return (ulong)takeFrames; // play out, then 0 ends the stream
        // Live underrun: pad with silence so the sound keeps running.
        int padSamples = (int)((frameCount - (ulong)takeFrames) * Channels);
        buffer.Slice(takeSamples, padSamples).Clear();
        return frameCount;
    }

    /// <summary>A live stream cannot rewind; only the no-op seek to the
    /// current position (the engine's initial seek to 0) succeeds.</summary>
    public override bool Seek(ulong frameIndex) =>
        frameIndex == (ulong)(Volatile.Read(ref _read) / Channels);
}
