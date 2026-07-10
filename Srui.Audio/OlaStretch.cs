namespace Srui.Audio;

/// <summary>
/// Offline time stretching (SOLA / WSOLA-lite), ported from cosmos-audio.
/// Produces a pitch-preserving, time-scaled copy of an interleaved f32 PCM
/// buffer by overlap-adding Hann-windowed grains with a small
/// cross-correlation search. Good for voicepack SFX at 0.5x..3.0x; runs at
/// load time, never on the audio thread.
/// </summary>
internal static class OlaStretch
{
    /// <summary>Target grain size at 48kHz (~43ms), scaled by sample rate.</summary>
    private const float TargetGrainMs = 43.0f;

    /// <summary>Overlap factor — hop = grain / 4 (75% overlap).</summary>
    private const int HopDivisor = 4;

    /// <summary>WSOLA search radius as a fraction of hop size.</summary>
    private const float SearchFraction = 0.5f;

    public static float[] Stretch(ReadOnlySpan<float> input, int channels, uint sampleRate, float factor)
    {
        if (input.IsEmpty || channels == 0 || factor <= 0.05f)
            return [];
        if (MathF.Abs(factor - 1.0f) < 1e-3f)
            return input.ToArray();

        var inputFrames = input.Length / channels;
        var outputFrames = (int)MathF.Ceiling(inputFrames * factor);
        if (outputFrames == 0)
            return [];

        var grain = Math.Min(
            Math.Clamp((int)(TargetGrainMs / 1000.0f * sampleRate), 256, 4096),
            Math.Max(inputFrames, 256));
        var hopOut = Math.Max(grain / HopDivisor, 1);
        var hopIn = (long)MathF.Round(hopOut / factor);
        var search = Math.Min((int)(hopOut * SearchFraction), (int)Math.Abs(hopIn));

        var window = HannWindow(grain);

        var output = new float[outputFrames * channels];
        // Per-frame window sum, for normalization.
        var norm = new float[outputFrames];

        // Reference tail for correlation.
        var overlapRef = grain / 2;

        long inPos = 0;
        var outPos = 0;
        var firstGrain = true;

        while (outPos + grain <= outputFrames)
        {
            var bestIn = firstGrain
                ? inPos
                : FindBestOffset(input, channels, inPos, search, output, outPos, overlapRef, inputFrames);

            // Overlap-add a Hann-windowed grain.
            for (var i = 0; i < grain; i++)
            {
                var srcFrame = bestIn + i;
                if (srcFrame < 0 || srcFrame >= inputFrames)
                    continue;
                var w = window[i];
                var outFrame = outPos + i;
                for (var c = 0; c < channels; c++)
                    output[outFrame * channels + c] += input[(int)srcFrame * channels + c] * w;
                norm[outFrame] += w;
            }

            inPos += hopIn;
            outPos += hopOut;
            firstGrain = false;

            if (inPos + grain > inputFrames + (long)search)
                break;
        }

        // Normalize by the summed window weight at each output frame.
        for (var i = 0; i < outputFrames; i++)
        {
            var n = norm[i];
            if (n > 1e-4f)
            {
                var inv = 1.0f / n;
                for (var c = 0; c < channels; c++)
                    output[i * channels + c] *= inv;
            }
        }

        return output;
    }

    private static float[] HannWindow(int len)
    {
        if (len <= 1)
        {
            var trivial = new float[len];
            Array.Fill(trivial, 1.0f);
            return trivial;
        }
        var window = new float[len];
        for (var i = 0; i < len; i++)
            window[i] = 0.5f - 0.5f * MathF.Cos(2.0f * MathF.PI * i / (len - 1));
        return window;
    }

    /// <summary>Find the input offset near `nominal` (±search frames) whose
    /// first overlapRef frames best correlate with the output tail.
    /// Normalized cross-correlation on the first channel only.</summary>
    private static long FindBestOffset(
        ReadOnlySpan<float> input,
        int channels,
        long nominal,
        int search,
        float[] output,
        int outPos,
        int overlapRef,
        int inputFrames)
    {
        if (search == 0)
            return nominal;
        var best = nominal;
        var bestScore = float.NegativeInfinity;

        var lo = Math.Max(nominal - search, 0);
        var hi = Math.Min(nominal + search, inputFrames - (long)overlapRef);

        var refStart = outPos * channels;

        var outEnergy = 0.0f;
        for (var i = 0; i < overlapRef; i++)
        {
            var s = output[refStart + i * channels];
            outEnergy += s * s;
        }
        if (outEnergy < 1e-8f)
            return nominal;

        for (var candidate = lo; candidate <= hi; candidate++)
        {
            var corr = 0.0f;
            var inEnergy = 0.0f;
            var basePos = (int)candidate * channels;
            for (var i = 0; i < overlapRef; i++)
            {
                var o = output[refStart + i * channels];
                var s = input[basePos + i * channels];
                corr += o * s;
                inEnergy += s * s;
            }
            var denom = MathF.Sqrt(outEnergy * inEnergy);
            var score = denom > 1e-8f ? corr / denom : float.NegativeInfinity;
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best;
    }
}
