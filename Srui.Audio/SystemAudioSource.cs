namespace Srui.Audio;

/// <summary>
/// The operating system's own decoder for the formats cosmos has no
/// native backend for (AAC, M4A, M4B, ALAC): Media Foundation on Windows
/// (<see cref="MediaFoundationAudioSource"/>) and AudioToolbox on macOS
/// (<see cref="AudioToolboxAudioSource"/>). A program that plays such
/// files on both platforms opens them here rather than naming either.
/// </summary>
public static class SystemAudioSource
{
    /// <summary>Open a file with the platform's decoder. Throws
    /// AudioException when the decoder cannot read it, and
    /// PlatformNotSupportedException on a platform with neither.</summary>
    public static PullAudioSource Open(string path)
    {
        if (OperatingSystem.IsWindows())
            return MediaFoundationAudioSource.Open(path);
        if (OperatingSystem.IsMacOS())
            return AudioToolboxAudioSource.Open(path);
        throw new PlatformNotSupportedException(
            "SystemAudioSource decodes through Media Foundation on Windows or AudioToolbox on macOS.");
    }

    /// <summary>Duration-only probe with the platform's decoder; 0 when
    /// the file is unreadable or the platform has no decoder.</summary>
    public static ulong ProbeDurationMs(string path)
    {
        if (OperatingSystem.IsWindows())
            return MediaFoundationAudioSource.ProbeDurationMs(path);
        if (OperatingSystem.IsMacOS())
            return AudioToolboxAudioSource.ProbeDurationMs(path);
        return 0;
    }
}
