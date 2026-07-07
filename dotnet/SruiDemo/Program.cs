// SRUI end-to-end demo, C# edition: SDL window in, Prism speech out.
//
// Tab / Shift+Tab move focus; Alt+arrows walk the hierarchy; arrows and
// typeahead work in the list; Space toggles the checkbox. Greet is the
// primary widget: Enter anywhere presses it (Ctrl+G too, as a host-side
// binding), and it reads the live name, list, and checkbox state.
// Escape (or Enter on Quit, or closing the window) exits.

using Srui;

using var app = new SruiApp("SRUI Demo (C#)");
Console.WriteLine($"speech backend: {app.Voice.BackendName}");

new Label(app, "SRUI demo, C sharp edition");
var name = new EditBox(app, "Your name");
new EditBox(app, "Notes", multiline: true);
var greet = new Button(app, "Greet");
var wrap = new CheckBox(app, "Word Wrap");
var options = new Group(app, "Options");
new CheckBox(options, "Autosave", isChecked: true);
new CheckBox(options, "Telemetry");
var fruits = new ListBox(
    app, "Fruits",
    ["apple", "banana", "cherry", "date", "elderberry"],
    numbered: true);
new Slider(app, "Volume", 50, 0, 100, unit: "%");
new TabControl(app, "Views", ["Library", "Playlist", "Effects"]);
new FilterListBox(
    app, "Commands",
    [
        "Save File", "Save As", "Open File", "Open Recent", "Close Tab",
        "Find", "Find Next", "Replace", "Go To Line", "Toggle Word Wrap",
        "Zoom In", "Zoom Out",
    ]);
new ShortcutField(app, "Custom shortcut");
var quit = new Button(app, "Quit");

// Enter anywhere presses Greet; Escape anywhere presses Quit.
app.SetPrimary(greet);
app.SetCancel(quit);

greet.Activated += Greet;
// Shift+Enter on Greet opens a reviewable status dialog.
greet.SecondaryActivated += () => app.ShowStatus(
    "About",
    "SRUI demo, C sharp edition.\nA screen-reader-first UI toolkit.\n"
        + "This text lives in a read-only edit box: arrows, words, and\n"
        + "selection all work. Escape or Close returns to the demo.");
quit.Activated += () => app.Confirm("Really quit?", onYes: app.Quit);

// Host-side bindings: Ctrl+G greets from anywhere.
app.UnhandledInput = input =>
{
    if (input.IsRawKey(Keys.Char('g'), Mods.Ctrl))
    {
        Greet();
        return true;
    }
    return false;
};

// A ticker: once a minute, note the uptime.
var minutes = 0;
var ticker = app.StartTicker(60_000);
ticker.Tick += () => app.Announce($"Demo running for {++minutes} minute{(minutes == 1 ? "" : "s")}.");

app.Run();
return;

// Compose and queue the greeting from live widget state.
void Greet()
{
    var who = string.IsNullOrEmpty(name.Text) ? "stranger" : name.Text;
    var fruit = fruits.SelectedItem ?? "nothing";
    app.Announce(
        $"Hello, {who}. The fruit is {fruit}, and word wrap is {(wrap.Checked ? "on" : "off")}.");
}
