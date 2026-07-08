namespace Srui.Core;

/// <summary>Platform clipboard access — injected by the host per the
/// sans-IO rule; every edit box gets it for free.</summary>
internal interface IClipboard
{
    string? Read();
    void Write(string text);
}

/// <summary>No-op clipboard for tests and headless use.</summary>
internal sealed class NoClipboard : IClipboard
{
    public string? Read() => null;
    public void Write(string text) { }
}
