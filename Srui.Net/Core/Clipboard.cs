namespace Srui;

/// <summary>Platform clipboard access — injected by the host per the
/// sans-IO rule; every edit box gets it for free. A windowed app installs
/// the SDL system clipboard; a headless host installs its own with
/// <see cref="SruiApp.SetClipboard"/> (the default is a no-op).</summary>
public interface IClipboard
{
    string? Read();
    void Write(string text);
}

/// <summary>No-op clipboard for headless use without an injected one.</summary>
internal sealed class NoClipboard : IClipboard
{
    public string? Read() => null;
    public void Write(string text) { }
}
