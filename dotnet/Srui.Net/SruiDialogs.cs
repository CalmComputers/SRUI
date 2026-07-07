namespace Srui;

/// <summary>
/// Canned dialogs. All follow the same accessible pattern: the prompt is
/// a Label preceding the widgets, so the opening announcement reads
/// "prompt, first widget". Escape dismisses (raising Dismissed) unless
/// noted. Results arrive through callbacks; there is no nested loop.
/// </summary>
public static class SruiDialogs
{
    /// <summary>A message with a single acknowledgement button.</summary>
    public static Dialog ShowMessage(this SruiApp app, string message, string buttonName = "OK")
    {
        var dialog = app.OpenDialog();
        _ = new Label(dialog, message);
        var ok = new Button(dialog, buttonName);
        ok.Activated += dialog.Close;
        app.SetPrimary(ok);
        dialog.AnnounceOpened();
        return dialog;
    }

    /// <summary>A yes/no question. Escape counts as no.</summary>
    public static Dialog Confirm(
        this SruiApp app, string question, Action onYes, Action? onNo = null)
    {
        var dialog = app.OpenDialog();
        _ = new Label(dialog, question);
        var yes = new Button(dialog, "Yes");
        var no = new Button(dialog, "No");
        var answered = false;
        yes.Activated += () =>
        {
            answered = true;
            dialog.Close();
            onYes();
        };
        no.Activated += () =>
        {
            answered = true;
            dialog.Close();
            onNo?.Invoke();
        };
        dialog.Dismissed += () =>
        {
            if (!answered) onNo?.Invoke();
        };
        app.SetPrimary(yes);
        dialog.AnnounceOpened();
        return dialog;
    }

    /// <summary>A message with a custom button row. The chosen button's
    /// name is reported; null means dismissed via Escape.</summary>
    public static Dialog ShowButtons(
        this SruiApp app, string message, IReadOnlyList<string> buttons, Action<string?> onChoice)
    {
        var dialog = app.OpenDialog();
        _ = new Label(dialog, message);
        var chosen = false;
        foreach (var name in buttons)
        {
            var captured = name;
            var button = new Button(dialog, captured);
            button.Activated += () =>
            {
                chosen = true;
                dialog.Close();
                onChoice(captured);
            };
        }
        dialog.Dismissed += () =>
        {
            if (!chosen) onChoice(null);
        };
        dialog.AnnounceOpened();
        return dialog;
    }

    /// <summary>Reviewable status text: a read-only multiline edit box
    /// (full cursor navigation over the text) plus a Close button.</summary>
    public static Dialog ShowStatus(this SruiApp app, string title, string text)
    {
        var dialog = app.OpenDialog();
        _ = new Label(dialog, title);
        // Nameless: the title Label carries identity as a context label,
        // so the announcement reads "title, edit read only multi line ..."
        // rather than doubling the title.
        var body = new EditBox(dialog, "", text, multiline: true) { ReadOnly = true };
        var close = new Button(dialog, "Close");
        close.Activated += dialog.Close;
        app.SetCancel(close);
        body.Focus();
        app.Ui.ReannounceWithContext();
        return dialog;
    }
}
