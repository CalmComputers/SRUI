namespace Srui.Audio;

/// <summary>A file decoded whole by <see cref="Sound.Decode"/>: its samples
/// interleaved frame by frame, as 32-bit floats, its channel count, and the
/// rate it was recorded at.</summary>
public sealed record DecodedAudio(float[] Samples, int Channels, int SampleRate)
{
    /// <summary>Frames in each channel.</summary>
    public int Frames => Samples.Length / Channels;
}
