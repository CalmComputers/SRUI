using Srui;

namespace MyApp;

/// <summary>Builds the interface. Program runs it in a window; the
/// tests run the same build headless and assert what it speaks.</summary>
public static class Ui
{
    public static void Build(SruiApp app)
    {
        _ = new Label(app, "MyApp");
        var name = new EditBox(app, "Your name");
        var greet = new Button(app, "Greet");
        var quit = new Button(app, "Quit");

        // Enter anywhere presses Greet; Escape anywhere presses Quit.
        app.SetPrimary(greet);
        app.SetCancel(quit);

        greet.Activated += () => app.Announce(
            name.Text.Length == 0 ? "Hello, stranger." : $"Hello, {name.Text}.");
        quit.Activated += app.Quit;
    }
}
