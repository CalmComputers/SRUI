namespace Srui.Audio;

/// <summary>Volume and pan conversion utilities (NVGT-compatible).</summary>
public static class Volume
{
    /// <summary>Linear volume to dB; -100 dB for zero or negative input.</summary>
    public static float LinearToDb(float linear) =>
        linear <= 0.0f ? -100.0f : 20.0f * MathF.Log10(linear);

    /// <summary>dB to linear volume.</summary>
    public static float DbToLinear(float db) => MathF.Pow(10.0f, db / 20.0f);

    /// <summary>dB pan to linear (-1..1), NVGT-style.</summary>
    public static float PanDbToLinear(float db)
    {
        db = Math.Clamp(db, -100.0f, 100.0f);
        var l = DbToLinear(-MathF.Abs(db));
        return db > 0.0f ? 1.0f - l : -1.0f + l;
    }

    /// <summary>Linear pan (-1..1) to dB, NVGT-style.</summary>
    public static float PanLinearToDb(float linear)
    {
        linear = Math.Clamp(linear, -1.0f, 1.0f);
        var db = LinearToDb(linear > 0.0f ? 1.0f - linear : linear + 1.0f);
        return linear > 0.0f ? -db : db;
    }
}
