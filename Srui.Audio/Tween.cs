using System.Diagnostics;

namespace Srui.Audio;

/// <summary>Easing curves for value automation (e.g. pitch tweens).</summary>
public enum Easing
{
    Linear,
    EaseIn,
    EaseOut,
    EaseInOut,
}

internal static class EasingMath
{
    public static float Apply(Easing easing, float t)
    {
        t = Math.Clamp(t, 0.0f, 1.0f);
        switch (easing)
        {
            case Easing.EaseIn:
                return t * t;
            case Easing.EaseOut:
                return 1.0f - (1.0f - t) * (1.0f - t);
            case Easing.EaseInOut:
                if (t < 0.5f) return 2.0f * t * t;
                var u = -2.0f * t + 2.0f;
                return 1.0f - u * u / 2.0f;
            default:
                return t;
        }
    }
}

/// <summary>Eased interpolation over wall-clock time. A value type using
/// Stopwatch timestamps, so ticking allocates nothing.</summary>
internal struct PitchTween
{
    public float StartPitch;
    public float TargetPitch;
    public long StartedTimestamp;
    public double DurationSeconds;
    public Easing Easing;

    public static PitchTween Start(float from, float target, TimeSpan duration, Easing easing) =>
        new()
        {
            StartPitch = from,
            TargetPitch = target,
            StartedTimestamp = Stopwatch.GetTimestamp(),
            DurationSeconds = duration.TotalSeconds,
            Easing = easing,
        };

    /// <summary>Returns the current value; `finished` true once complete.</summary>
    public readonly float Sample(out bool finished)
    {
        if (DurationSeconds <= 0.0)
        {
            finished = true;
            return TargetPitch;
        }
        var elapsed = Stopwatch.GetElapsedTime(StartedTimestamp).TotalSeconds;
        if (elapsed >= DurationSeconds)
        {
            finished = true;
            return TargetPitch;
        }
        finished = false;
        var eased = EasingMath.Apply(Easing, (float)(elapsed / DurationSeconds));
        return StartPitch + (TargetPitch - StartPitch) * eased;
    }
}
