using Srui.Audio;
using Xunit;

namespace Srui.Net.Tests;

/// <summary>The push-fed ring source, driven directly (no native
/// engine): write/read round-trips, wraparound, silence on underrun,
/// play-out and end-of-stream after Complete, and seek refusal.</summary>
public class PushAudioSourceTests
{
    private static float[] Ramp(int samples, float start = 0f)
    {
        var data = new float[samples];
        for (int i = 0; i < samples; i++)
            data[i] = start + i;
        return data;
    }

    [Fact]
    public void RoundTripsSamplesInOrder()
    {
        using var source = new PushAudioSource(channels: 2, sampleRate: 48000, capacityFrames: 64);
        Assert.Equal(16, source.Write(Ramp(16)));
        Assert.Equal(8u, source.BufferedFrames);

        var buffer = new float[16];
        Assert.Equal(8ul, source.Read(buffer, 8));
        Assert.Equal(Ramp(16), buffer);
        Assert.Equal(0u, source.BufferedFrames);
    }

    [Fact]
    public void UnderrunPadsWithSilenceWhileLive()
    {
        using var source = new PushAudioSource(1, 48000, 32);
        source.Write(Ramp(4, start: 1f));

        var buffer = new float[8];
        Array.Fill(buffer, -1f);
        // Live stream: a short ring still fills the whole request.
        Assert.Equal(8ul, source.Read(buffer, 8));
        Assert.Equal(new float[] { 1, 2, 3, 4, 0, 0, 0, 0 }, buffer);

        // Fully empty reads are all silence, still frameCount frames.
        Assert.Equal(8ul, source.Read(buffer, 8));
        Assert.Equal(new float[8], buffer);
    }

    [Fact]
    public void CompletePlaysOutThenEndsTheStream()
    {
        using var source = new PushAudioSource(2, 44100, 16);
        source.Write(Ramp(6));
        source.Complete();
        Assert.True(source.IsCompleted);

        var buffer = new float[16];
        Assert.Equal(3ul, source.Read(buffer, 8)); // partial: the tail
        Assert.Equal(0ul, source.Read(buffer, 8)); // then end of stream
    }

    [Fact]
    public void WriteStopsAtCapacityAndResumesAfterRead()
    {
        using var source = new PushAudioSource(1, 48000, 8);
        Assert.Equal(8, source.Write(Ramp(12)));
        Assert.Equal(0, source.Write(Ramp(1)));

        var buffer = new float[4];
        Assert.Equal(4ul, source.Read(buffer, 4));
        Assert.Equal(4, source.Write(Ramp(12, start: 100f)));
    }

    [Fact]
    public void WrapAroundPreservesOrder()
    {
        using var source = new PushAudioSource(1, 48000, 8);
        var scratch = new float[8];
        // Advance the ring so writes and reads straddle the boundary.
        for (int round = 0; round < 5; round++)
        {
            Assert.Equal(6, source.Write(Ramp(6, start: round * 10f)));
            Assert.Equal(6ul, source.Read(scratch, 6));
            Assert.Equal(Ramp(6, start: round * 10f), scratch[..6]);
        }
    }

    [Fact]
    public void PartialFrameIsHeldUntilItsLastSampleArrives()
    {
        using var source = new PushAudioSource(2, 48000, 16);
        source.Write(Ramp(3)); // one and a half frames
        Assert.Equal(1u, source.BufferedFrames);

        var buffer = new float[4];
        Array.Fill(buffer, -1f);
        Assert.Equal(2ul, source.Read(buffer, 2)); // frame 1 + silence pad
        Assert.Equal(new float[] { 0, 1, 0, 0 }, buffer);

        source.Write(Ramp(1, start: 3f)); // the missing half
        Assert.Equal(1u, source.BufferedFrames);
        Assert.Equal(2ul, source.Read(buffer, 2));
        Assert.Equal(new float[] { 2, 3, 0, 0 }, buffer);
    }

    [Fact]
    public void SeekOnlyAcceptsTheCurrentPosition()
    {
        using var source = new PushAudioSource(2, 48000, 16);
        Assert.True(source.Seek(0)); // the engine's initial seek
        Assert.False(source.Seek(1));

        source.Write(Ramp(8));
        var buffer = new float[8];
        source.Read(buffer, 4);
        Assert.True(source.Seek(4));
        Assert.False(source.Seek(0));
    }
}
