using System.Runtime.InteropServices;

namespace Srui.Core;

/// <summary>Hand-written P/Invoke over the slice of Prism's C API the
/// speech channel uses: context lifecycle, best-backend acquisition, and
/// output. prism.dll ships alongside the app (built by the native build,
/// same as SDL3 and cosmos).</summary>
internal static class PrismNative
{
    private const string Dll = "prism";

    public const int Ok = 0;
    public const int ErrorAlreadyInitialized = 15;

    /// <summary>PrismConfig, version 3. Byte-for-byte layout of the C
    /// struct; prism_config_init fills the defaults.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Config
    {
        public byte Version;
        public IntPtr Registry;
        public IntPtr AvailabilityCallback;
        public IntPtr AvailabilityUserdata;
        public uint AvailabilityPollIntervalMs;
        public uint AvailabilityDebounceSamples;
        public uint AvailabilityBackoffMaxMs;
        public byte AvailabilityAutoPowerManage;
    }

    [DllImport(Dll)]
    public static extern Config prism_config_init();

    [DllImport(Dll)]
    public static extern IntPtr prism_init(ref Config cfg);

    [DllImport(Dll)]
    public static extern void prism_shutdown(IntPtr ctx);

    [DllImport(Dll)]
    public static extern IntPtr prism_registry_create_best(IntPtr ctx);

    [DllImport(Dll)]
    public static extern void prism_backend_free(IntPtr backend);

    [DllImport(Dll)]
    public static extern IntPtr prism_backend_name(IntPtr backend);

    [DllImport(Dll)]
    public static extern int prism_backend_initialize(IntPtr backend);

    [DllImport(Dll)]
    public static extern int prism_backend_speak(
        IntPtr backend, [MarshalAs(UnmanagedType.LPUTF8Str)] string text,
        [MarshalAs(UnmanagedType.I1)] bool interrupt);

    [DllImport(Dll)]
    public static extern int prism_backend_braille(
        IntPtr backend, [MarshalAs(UnmanagedType.LPUTF8Str)] string text);

    [DllImport(Dll)]
    public static extern int prism_backend_output(
        IntPtr backend, [MarshalAs(UnmanagedType.LPUTF8Str)] string text,
        [MarshalAs(UnmanagedType.I1)] bool interrupt);

    [DllImport(Dll)]
    public static extern int prism_backend_stop(IntPtr backend);

    [DllImport(Dll)]
    public static extern int prism_backend_is_speaking(
        IntPtr backend, [MarshalAs(UnmanagedType.I1)] out bool speaking);
}
