namespace Srui;

/// <summary>
/// A modal layer. Widgets created with the dialog as their container
/// live in it; only the dialog is navigable while open. Escape closes it
/// automatically (raising <see cref="Dismissed"/>) unless a cancel
/// widget was set, in which case that widget's activation is in charge.
/// Announce the opening with <see cref="AnnounceOpened"/> after focusing.
/// The result pattern: deliver the result first, then <see cref="Close"/>
/// - the closing restore then speaks whatever the delivery changed, and
/// a dialog the delivery opened defers the close until it dies (the
/// cascade below).
/// </summary>
public sealed class Dialog : IWidgetContainer
{
    public SruiApp App { get; }

    /// <summary>Escape closed the dialog (no explicit choice was made).</summary>
    public event Action? Dismissed;

    /// <summary>The dialog was closed, by any route - fired when its
    /// layer actually collapses, so a condemned dialog's handlers run
    /// once the cascade reaches it.</summary>
    public event Action? Closed;

    public bool IsOpen { get; private set; }

    /// <summary>Close was called while dialogs lived above; the layer
    /// stands until the cascade reaches it.</summary>
    internal bool Condemned;

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

    /// <summary>Close the dialog; the previous focus is restored, and
    /// the restore speaks what changed while the dialog was open. With
    /// dialogs still open above this one - the result delivery opened
    /// its own UI - the close is deferred: the dialog is condemned and
    /// collapses automatically when the last dialog above it dies,
    /// with focus landing where the whole excursion left it. Safe to
    /// call twice.</summary>
    public void Close() => Close(nested: false);

    /// <summary>Close with <paramref name="nested"/> true to take the
    /// dialogs still open above this one down with it immediately,
    /// instead of waiting for them.</summary>
    public void Close(bool nested)
    {
        if (!IsOpen) return;
        App.CloseDialog(this, nested);
    }

    /// <summary>The layer actually popped.</summary>
    internal void Collapse()
    {
        IsOpen = false;
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
