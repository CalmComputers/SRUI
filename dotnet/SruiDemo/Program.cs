// SRUI end-to-end demo, C# edition: SDL window in, Prism speech out.
//
// Tab / Shift+Tab move focus; Alt+arrows walk the hierarchy; arrows and
// typeahead work in the list; Space toggles the checkbox. Greet is the
// primary widget: Enter anywhere presses it (Ctrl+G too, as a host-side
// binding), and it reads the live name, list, and checkbox state.
// Escape (or Enter on Quit, or closing the window) exits.

using System.Diagnostics;
using Srui;

using var host = new SdlHost("SRUI Demo (C#)", 400, 300);
using var voice = new Speech();
Console.WriteLine($"speech backend: {voice.BackendName}");

using var ui = new Ui();
host.ProvideClipboard(ui);
ui.TextLabel(NodeId.None, "SRUI demo, C sharp edition");
var nameField = ui.Editbox(NodeId.None, "Your name");
ui.Editbox(NodeId.None, "Notes", multiline: true);
var greetBtn = ui.Button(NodeId.None, "Greet");
var wrap = ui.Checkbox(NodeId.None, "Word Wrap", false);
var options = ui.Group(NodeId.None, "Options");
ui.Checkbox(options, "Autosave", true);
ui.Checkbox(options, "Telemetry", false);
var fruits = ui.Listbox(
    NodeId.None,
    "Fruits",
    ["apple", "banana", "cherry", "date", "elderberry"],
    numbered: true);
ui.Slider(NodeId.None, "Volume", 50, 0, 100, unit: "%");
ui.TabControl(NodeId.None, "Views", ["Library", "Playlist", "Effects"]);
ui.FilterListbox(
    NodeId.None,
    "Commands",
    [
        "Save File", "Save As", "Open File", "Open Recent", "Close Tab",
        "Find", "Find Next", "Replace", "Go To Line", "Toggle Word Wrap",
        "Zoom In", "Zoom Out",
    ]);
ui.ShortcutField(NodeId.None, "Custom shortcut");
var quit = ui.Button(NodeId.None, "Quit");
// Enter anywhere presses Greet; Escape anywhere presses Quit.
ui.SetPrimary(greetBtn);
ui.SetCancel(quit);
ui.EnsureFocus();

var clock = Stopwatch.StartNew();
var running = true;
while (running)
{
    foreach (var hostEvent in host.Pump(5))
    {
        switch (hostEvent)
        {
            case HostEvent.Quit:
                running = false;
                break;
            case HostEvent.KeyDown:
                voice.Stop();
                break;
            case HostEvent.AltTap:
                break;
            case HostEvent.Input(var input):
                ui.SetNow((ulong)clock.ElapsedMilliseconds);
                if (!ui.HandleInput(input))
                {
                    // Host-side bindings: unconsumed input is ours to
                    // match. Ctrl+G greets from anywhere.
                    if (input.IsRawKey(Keys.Char('g'), Mods.Ctrl))
                        Greet();
                }
                break;
        }
    }

    // Drain until quiescent: reactions to widget events (the Greet
    // announcement) queue further output that must be spoken this
    // iteration, not after the next pump.
    while (true)
    {
        var batch = ui.Drain();
        if (batch.Count == 0) break;
        foreach (var output in batch)
        {
            switch (output)
            {
                case OutputEvent.Speech(var text, _, _):
                    voice.Speak(text);
                    break;
                case OutputEvent.Activated(var node) when node == quit:
                    running = false;
                    break;
                case OutputEvent.Activated(var node) when node == greetBtn:
                    Greet();
                    break;
            }
        }
    }
}

return;

// Compose and queue the greeting from live widget state.
void Greet()
{
    var fruit = ui.ListboxSelectedItem(fruits) ?? "nothing";
    var wrapped = ui.CheckboxChecked(wrap);
    var name = ui.EditboxText(nameField);
    var who = string.IsNullOrEmpty(name) ? "stranger" : name;
    ui.Announce(
        $"Hello, {who}. The fruit is {fruit}, and word wrap is {(wrapped ? "on" : "off")}.");
}
