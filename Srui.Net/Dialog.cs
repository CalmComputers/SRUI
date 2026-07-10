namespace Srui;

/// <summary>
/// A modal layer. Widgets created with the dialog as their container
/// live in it; only the dialog is navigable while open. Escape closes it
/// automatically (raising <see cref="Dismissed"/>) unless a cancel
/// widget was set, in which case that widget's activation is in charge.
/// Announce the opening with <see cref="AnnounceOpened"/> after focusing.
/// </summary>
public sealed class Dialog : IWidgetContainer
{
    public SruiApp App { get; }

    /// <summary>Escape closed the dialog (no explicit choice was made).</summary>
    public event Action? Dismissed;

    /// <summary>The dialog was closed, by any route.</summary>
    public event Action? Closed;

    public bool IsOpen { get; private set; }

    internal Dialog(SruiApp app)
    {
        App = app;
        App.Engine.PushLayer();
        IsOpen = true;
    }

    /// <summary>Focus the dialog's first focusable widget and announce
    /// it with its context labels, so the prompt is heard: "Delete 3
    /// files? Yes button".</summary>
    public void AnnounceOpened()
    {
        App.EnsureFocus();
        App.ReannounceWithContext();
    }

    /// <summary>Pop the layer; the previous focus is restored and
    /// announced. Safe to call twice.</summary>
    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        App.CloseDialog(this);
        Closed?.Invoke();
    }

    /// <summary>Close via Escape: raises Dismissed, then Closed.</summary>
    internal void Dismiss()
    {
        if (!IsOpen) return;
        Dismissed?.Invoke();
        Close();
    }
}
