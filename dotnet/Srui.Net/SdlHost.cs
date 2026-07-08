namespace Srui;

/// <summary>The SDL window and event pump (keyboard focus surface).</summary>
public sealed class SdlHost : IDisposable
{
    private IntPtr _handle;

    public SdlHost(string title, uint width = 400, uint height = 300)
    {
        _handle = NativeMethods.srui_host_new(title, width, height);
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("SDL window creation failed");
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            NativeMethods.srui_host_free(_handle);
            _handle = IntPtr.Zero;
        }
    }

    /// <summary>Install this host's system clipboard on a Ui.</summary>
    public void ProvideClipboard(Ui ui) =>
        NativeMethods.srui_ui_use_host_clipboard(ui.Handle, _handle);

    // Shared result for empty batches: the pump runs a few hundred times
    // a second and is almost always empty, so returning a fresh list per
    // call would put a steady drip of garbage under an idle app. The
    // concrete List return type (rather than IReadOnlyList) keeps
    // foreach on the struct enumerator, which is also allocation-free.
    private static readonly List<HostEvent> EmptyBatch = new();

    /// <summary>Block up to the timeout for events, then drain the batch.
    /// Treat the result as read-only: empty batches are shared.</summary>
    public unsafe List<HostEvent> Pump(uint timeoutMs)
    {
        NativeMethods.srui_host_pump(_handle, timeoutMs, out var events, out var len);
        try
        {
            if (len == 0)
                return EmptyBatch;
            var result = new List<HostEvent>((int)len);
            for (nuint i = 0; i < len; i++)
            {
                var e = events[i];
                result.Add(e.Kind switch
                {
                    0 => new HostEvent.Quit(),
                    1 => new HostEvent.KeyDown(),
                    2 => new HostEvent.AltTap(),
                    // Physical key transition: input_kind is flags
                    // (bit 0 pressed, bit 1 repeat).
                    4 => new HostEvent.Key(new KeyInput(
                        e.Key,
                        (Mods)e.Mods,
                        (e.InputKind & 1) == 0 ? KeyPhase.Release
                            : (e.InputKind & 2) != 0 ? KeyPhase.Repeat
                            : KeyPhase.Press)),
                    5 => new HostEvent.FocusLost(),
                    _ => new HostEvent.Input(new InputEvent((InputKind)e.InputKind, e.Ch, e.Key, (Mods)e.Mods)),
                });
            }
            return result;
        }
        finally
        {
            NativeMethods.srui_host_events_free(events, len);
        }
    }
}
