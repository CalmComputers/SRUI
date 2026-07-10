using Srui.Core;

namespace Srui;

/// <summary>The SDL window and event pump (keyboard focus surface): a
/// hidden-rendering window that exists to receive keyboard focus, plus
/// the physical → logical input translation.</summary>
public sealed class SdlHost : IDisposable
{
    private IntPtr _window;
    private readonly InputMapper _mapper = new();

    public SdlHost(string title, uint width = 400, uint height = 300)
    {
        if (!Sdl3.SDL_Init(Sdl3.InitVideo))
            throw new InvalidOperationException($"SDL init failed: {Sdl3.GetError()}");
        _window = Sdl3.SDL_CreateWindow(title, (int)width, (int)height, 0);
        if (_window == IntPtr.Zero)
        {
            Sdl3.SDL_Quit();
            throw new InvalidOperationException($"SDL window creation failed: {Sdl3.GetError()}");
        }
        // Required for TypeChar (text input) events.
        Sdl3.SDL_StartTextInput(_window);
    }

    public void Dispose()
    {
        if (_window != IntPtr.Zero)
        {
            Sdl3.SDL_DestroyWindow(_window);
            _window = IntPtr.Zero;
            Sdl3.SDL_Quit();
        }
    }

    /// <summary>Install this host's system clipboard on an app's engine.</summary>
    public void ProvideClipboard(SruiApp app) => app.Engine.SetClipboard(new SdlClipboard());

    // Shared result for empty batches: the pump runs a few hundred times
    // a second and is almost always empty, so returning a fresh list per
    // call would put a steady drip of garbage under an idle app. The
    // concrete List return type (rather than IReadOnlyList) keeps
    // foreach on the struct enumerator, which is also allocation-free.
    private static readonly List<HostEvent> EmptyBatch = new();

    /// <summary>Block up to the timeout for events, then drain the batch.
    /// Treat the result as read-only: empty batches are shared.</summary>
    public List<HostEvent> Pump(uint timeoutMs)
    {
        if (!Sdl3.SDL_WaitEventTimeout(out var ev, (int)timeoutMs))
            return EmptyBatch;

        var result = new List<HostEvent>();
        Dispatch(in ev, result);
        while (Sdl3.SDL_PollEvent(out ev))
            Dispatch(in ev, result);
        // Alt tap resolves only after the whole batch is seen, so a
        // FocusLost in the same batch can cancel it.
        if (_mapper.TakeAltTap())
            result.Add(new HostEvent.AltTap());
        return result;
    }

    private void Dispatch(in Sdl3.Event ev, List<HostEvent> output)
    {
        if (ev.Type == Sdl3.EventQuit)
        {
            output.Add(new HostEvent.Quit());
            return;
        }
        if (ev.Type == Sdl3.EventKeyDown)
            output.Add(new HostEvent.KeyDown());

        // The physical key stream, ahead of the logical Input so a game
        // reacts before the input's side effects land.
        switch (ev.Type)
        {
            case Sdl3.EventKeyDown:
                if (InputMapper.PhysicalCombo(ev.Key, ev.Mod) is (var downKey, var downMods))
                    output.Add(new HostEvent.Key(new KeyInput(
                        downKey, downMods,
                        ev.Repeat != 0 ? KeyPhase.Repeat : KeyPhase.Press)));
                break;
            case Sdl3.EventKeyUp:
                if (InputMapper.PhysicalCombo(ev.Key, ev.Mod) is (var upKey, var upMods))
                    output.Add(new HostEvent.Key(new KeyInput(upKey, upMods, KeyPhase.Release)));
                break;
            case Sdl3.EventWindowFocusLost:
                output.Add(new HostEvent.FocusLost());
                break;
        }

        if (_mapper.Map(in ev) is InputEvent input)
            output.Add(new HostEvent.Input(input));
    }
}

/// <summary>The system clipboard via SDL.</summary>
internal sealed class SdlClipboard : IClipboard
{
    public string? Read()
    {
        var ptr = Sdl3.SDL_GetClipboardText();
        if (ptr == IntPtr.Zero)
            return null;
        try
        {
            var text = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(ptr);
            return string.IsNullOrEmpty(text) ? null : text;
        }
        finally
        {
            Sdl3.SDL_free(ptr);
        }
    }

    public void Write(string text) => Sdl3.SDL_SetClipboardText(text);
}
