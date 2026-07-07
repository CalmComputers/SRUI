// SRUI end-to-end demo, C# edition: SDL window in, Prism speech and
// Srui.Audio sound out.
//
// A widget gallery arranged as tab-switched panels: the Views tabs show
// one panel at a time (Editor, Lists, Dialogs, Dynamic, Game),
// exercising every widget type, all canned and custom dialogs, dynamic
// state, and game-style press/release input on the Game panel.
// Greet is the primary widget: Enter anywhere presses it (Ctrl+G too, as
// a host-side binding). Widget shortcuts: Alt+V jumps to the view
// switcher, Ctrl+Q presses Quit, Ctrl+J starts the job (Dynamic panel
// only). Every widget event is recorded with a running
// counter; the Event log button (or Ctrl+L) opens a reviewable dump,
// most recent first. Lists are sound-augmented (SoundListBox /
// SoundFilterListBox): navigation pings, positioned by item index.
// Escape (or Enter on Quit, or closing the window) exits.

using Srui;
using Srui.Audio;
using SruiDemo;

using var app = new SruiApp("SRUI Demo (C#)");
Console.WriteLine($"speech backend: {app.Voice.BackendName}");

// ── Audio ──

using var audio = new SoundManager();
// Cosmos convention: angle 0 faces +X (east). Face +Y (north/forward)
// so item positions along X read as left/right.
audio.SetListener(0.0f, 0.0f, 0.0f, 90.0f);
using var sfxBus = audio.CreateGroup();
var pingWav = Path.Combine(Path.GetTempPath(), "srui-demo-ping.wav");
WritePingWav(pingWav);
using var navSound = new ListNavSound(audio, sfxBus, pingWav);

// ── Event log ──

var log = new List<string>();
var eventCount = 0;
void Log(string entry)
{
    log.Add($"{++eventCount}. {entry}");
    if (log.Count > 200)
        log.RemoveAt(0);
}

// ── Root: title, view switcher, panels, global buttons ──

new Label(app, "SRUI demo, C sharp edition");
string[] panelNames = ["Editor", "Lists", "Dialogs", "Dynamic", "Game"];
var views = new TabControl(app, "Views", panelNames);

var editorPanel = new Group(app, "Editor");
var listsPanel = new Group(app, "Lists");
var dialogsPanel = new Group(app, "Dialogs");
var dynamicPanel = new Group(app, "Dynamic");
var gamePanel = new Group(app, "Game");
Group[] panels = [editorPanel, listsPanel, dialogsPanel, dynamicPanel, gamePanel];

var greet = new Button(app, "Greet");
var showLog = new Button(app, "Event log");
var quit = new Button(app, "Quit");

// Enter anywhere presses Greet; Escape anywhere presses Quit. They stay
// at the root, always visible, so both work from every panel (a hidden
// or disabled primary/cancel would not activate).
app.SetPrimary(greet);
app.SetCancel(quit);

// The tabs show one panel at a time; hidden panels leave the tab ring.
for (var i = 1; i < panels.Length; i++)
    panels[i].Hidden = true;
views.Changed += () =>
{
    var active = views.ActiveIndex;
    for (var i = 0; i < panels.Length; i++)
        panels[i].Hidden = i != active;
    Log($"views: {panelNames[active]}");
};

// ── Editor panel: text, toggles, capture ──

var name = new EditBox(editorPanel, "Your name");
var notes = new EditBox(editorPanel, "Notes", multiline: true);
var wrap = new CheckBox(editorPanel, "Word wrap");
var options = new Group(editorPanel, "Options");
var autosave = new CheckBox(options, "Autosave", isChecked: true);
var telemetry = new CheckBox(options, "Telemetry");
var shortcut = new ShortcutField(editorPanel, "Custom shortcut");

name.Changed += () => Log($"name changed: \"{name.Text}\"");
notes.Changed += () => Log("notes changed");
wrap.Toggled += on => Log($"word wrap: {(on ? "on" : "off")}");
autosave.Toggled += on => Log($"autosave: {(on ? "on" : "off")}");
telemetry.Toggled += on => Log($"telemetry: {(on ? "on" : "off")}");
shortcut.Changed += () => Log($"shortcut: {shortcut.Combo ?? "cleared"}");

// ── Lists panel: sound-augmented lists, live items, the SFX bus ──

var fruitItems = new List<string> { "apple", "banana", "cherry", "date", "elderberry" };
var fruits = new SoundListBox(listsPanel, "Fruits", fruitItems, navSound, numbered: true);
var rotate = new Button(listsPanel, "Rotate fruits");
var commands = new SoundFilterListBox(
    listsPanel, "Commands",
    [
        "Save File", "Save As", "Open File", "Open Recent", "Close Tab",
        "Find", "Find Next", "Replace", "Go To Line", "Toggle Word Wrap",
        "Zoom In", "Zoom Out",
    ],
    navSound);
var effect = new SoundListBox(listsPanel, "Bus effect", ["Dry", "Echo", "Reverb"], navSound);
var volume = new Slider(listsPanel, "Volume", 50, 0, 100, unit: "%");

fruits.Changed += () => Log($"fruits: {fruits.SelectedIndex}, {fruits.SelectedItem}");
rotate.Activated += () =>
{
    fruitItems.Add(fruitItems[0]);
    fruitItems.RemoveAt(0);
    fruits.SetItems(fruitItems);
    Log($"fruits rotated: first is {fruitItems[0]}");
    app.Announce($"Rotated. First fruit: {fruitItems[0]}.");
};
commands.Changed += () => Log($"commands: {commands.SelectedItem ?? "no match"}");
effect.Changed += () =>
{
    ApplyEffect(effect.SelectedIndex);
    Log($"bus effect: {effect.SelectedItem}");
};
sfxBus.Volume = volume.Value / 100.0f;
volume.Changed += () =>
{
    sfxBus.Volume = volume.Value / 100.0f;
    Log($"volume: {volume.Value}%");
};

// ── Dialogs panel: every canned dialog plus custom layered ones ──

var msgButton = new Button(dialogsPanel, "Message");
var confirmButton = new Button(dialogsPanel, "Confirm");
var chooseButton = new Button(dialogsPanel, "Choose");
var statusButton = new Button(dialogsPanel, "Status");
var formButton = new Button(dialogsPanel, "Form");
var nestedButton = new Button(dialogsPanel, "Nested");

msgButton.Activated += () =>
{
    Log("message: opened");
    var dialog = app.ShowMessage("Operation complete.");
    dialog.Closed += () => Log("message: closed");
};
confirmButton.Activated += () => app.Confirm(
    "Overwrite the existing file?",
    onYes: () =>
    {
        Log("confirm: yes");
        app.Announce("Overwritten.");
    },
    onNo: () =>
    {
        Log("confirm: no");
        app.Announce("Kept the original.");
    });
chooseButton.Activated += () => app.ShowButtons(
    "Close without saving?",
    ["Save", "Discard", "Cancel"],
    choice =>
    {
        Log($"choose: {choice ?? "dismissed"}");
        app.Announce(choice is null ? "Dismissed." : $"{choice}.");
    });
statusButton.Activated += () => app.ShowStatus(
    "Build report",
    "Compiled 14 files in 2.3 seconds.\nWarnings: 0. Errors: 0.\n"
        + "This text lives in a read-only edit box: arrows, words, and\n"
        + "selection all work. Escape or Close returns to the demo.");
formButton.Activated += OpenFormDialog;
nestedButton.Activated += OpenNestedDialog;

// ── Dynamic panel: state mutation, tickers, creation and removal ──

var scratch = new EditBox(dynamicPanel, "Scratch");
var showScratch = new CheckBox(dynamicPanel, "Show scratch", isChecked: true);
var enableScratch = new CheckBox(dynamicPanel, "Enable scratch", isChecked: true);
var rename = new Button(dynamicPanel, "Rename scratch");
var setText = new Button(dynamicPanel, "Set scratch text");
var progress = new Slider(dynamicPanel, "Progress", 0, 0, 100, unit: "%");
var startJob = new Button(dynamicPanel, "Start job");
var spawn = new Button(dynamicPanel, "Spawn a button");

showScratch.Toggled += on =>
{
    scratch.Hidden = !on;
    Log($"scratch hidden: {!on}");
};
enableScratch.Toggled += on =>
{
    scratch.Disabled = !on;
    Log($"scratch disabled: {!on}");
};
var renamed = false;
rename.Activated += () =>
{
    renamed = !renamed;
    var newName = renamed ? "Scratchpad" : "Scratch";
    scratch.SetName(newName);
    scratch.SetDescription(renamed ? "Renamed by the Rename button." : "");
    Log($"scratch renamed: {newName}");
    app.Announce($"Renamed to {newName}.");
};
var stamps = 0;
setText.Activated += () =>
{
    scratch.Text = $"stamp {++stamps}";
    Log($"scratch text set: stamp {stamps}");
    app.Announce($"Text set to stamp {stamps}.");
};

// A fake job: a 200ms ticker walks the Progress slider to 100, with the
// Start button disabled for the duration.
Ticker? job = null;
startJob.Activated += () =>
{
    if (job is not null)
        return;
    progress.Value = 0;
    // Focus the job explicitly before disabling the button under us —
    // otherwise focus recovery picks its own, surprising target.
    progress.Focus();
    startJob.Disabled = true;
    Log("job started");
    app.Announce("Job started.");
    job = app.StartTicker(200);
    job.Tick += () =>
    {
        progress.Value = Math.Min(progress.Value + 5, 100);
        if (progress.Value >= 100)
        {
            job!.Stop();
            job = null;
            startJob.Disabled = false;
            Log("job finished");
            app.Announce("Job finished.");
        }
    };
};

// Ephemeral buttons: created live, removed by pressing them (focus
// recovers to a surviving neighbor).
var spawned = 0;
spawn.Activated += () =>
{
    var n = ++spawned;
    var ephemeral = new Button(dynamicPanel, $"Ephemeral {n}");
    ephemeral.Activated += () =>
    {
        ephemeral.Remove();
        Log($"ephemeral {n} removed");
    };
    Log($"ephemeral {n} spawned");
    app.Announce($"Spawned ephemeral {n}.");
};

// ── Game panel: the physical key stream on a role-less widget ──

// The Arena is a CustomWidget: focusable, no spoken role, no built-in
// behavior. Interaction is entirely BindKey: hold an arrow to walk a
// nine-step lane (a ping marks each step's position), hold Q to draw a
// bow and release to loose.
var arena = new CustomWidget(gamePanel, "Arena");
arena.SetDescription("Hold Left or Right to walk. Hold Q to draw, release to loose.");
var drum = new Button(gamePanel, "Drum");

var arenaX = 0; // lane position, -4..4, audible as ping azimuth
var walkDir = 0;
var drawing = false;

void StartWalk(int dir, string announce)
{
    if (walkDir == dir)
        return;
    walkDir = dir;
    app.AnnounceNow(announce);
}
void StopWalk(int dir)
{
    if (walkDir != dir)
        return;
    walkDir = 0;
    app.Announce("Stopped.");
}
arena.BindKey("left", KeyPhase.Press, () => StartWalk(-1, "West."));
arena.BindKey("right", KeyPhase.Press, () => StartWalk(1, "East."));
arena.BindKey("left", KeyPhase.Release, () => StopWalk(-1));
arena.BindKey("right", KeyPhase.Release, () => StopWalk(1));
arena.BindKey("q", KeyPhase.Press, () =>
{
    drawing = true;
    app.AnnounceNow("Drawing.");
});
arena.BindKey("q", KeyPhase.Release, () =>
{
    if (!drawing)
        return;
    drawing = false;
    navSound.Play(arenaX + 4, 9);
    app.AnnounceNow("Loosed!");
    Log("arena: arrow loosed");
});

// Bindings work on every widget, not just role-less ones: the Drum
// button drums on D's press and release while it is focused (Enter and
// Space still press it normally).
drum.BindKey("d", KeyPhase.Press, () =>
{
    navSound.Play();
    app.AnnounceNow("Boom.");
});
drum.BindKey("d", KeyPhase.Release, () => app.Announce("Tss."));
drum.Activated += () =>
{
    navSound.Play();
    Log("drum pressed");
};

// Walking: one step per 150ms while a direction is held. Focus leaving
// the arena stops the walk — the release would no longer reach the
// arena's bindings.
var walkTicker = app.StartTicker(150);
walkTicker.Tick += () =>
{
    if (walkDir == 0)
        return;
    if (!arena.IsFocused)
    {
        walkDir = 0;
        return;
    }
    var next = Math.Clamp(arenaX + walkDir, -4, 4);
    if (next == arenaX)
    {
        walkDir = 0;
        app.AnnounceNow("Wall.");
        return;
    }
    arenaX = next;
    navSound.Play(arenaX + 4, 9);
};

// The window losing focus orphans held keys: zero the held-key state.
app.FocusLost = () =>
{
    walkDir = 0;
    drawing = false;
};

// ── Global buttons and bindings ──

greet.Activated += () =>
{
    Log("greet");
    Greet();
};
// Shift+Enter on Greet opens a reviewable status dialog.
greet.SecondaryActivated += () =>
{
    Log("greet: secondary");
    app.ShowStatus(
        "About",
        "SRUI demo, C sharp edition.\nA screen-reader-first UI toolkit.\n"
            + "Tab and Shift+Tab move focus; Alt+arrows walk the hierarchy;\n"
            + "the Views tabs switch panels (Alt+V jumps to them).\n"
            + "Ctrl+L opens the event log; Ctrl+Q quits.");
};
showLog.Activated += ShowEventLog;
quit.Activated += () => app.Confirm("Really quit?", onYes: app.Quit);

// Widget shortcuts, one of each flavor: Alt+V jumps to the view
// switcher, Ctrl+Q presses Quit from anywhere without moving focus, and
// Ctrl+J jumps to Start job and presses it. Start job lives on the
// Dynamic panel, so Ctrl+J is inert elsewhere (a hidden or disabled
// widget's shortcuts don't fire).
views.AddShortcut("alt+v");
quit.AddShortcut("ctrl+q", ShortcutAction.Activate);
startJob.AddShortcut("ctrl+j", ShortcutAction.JumpAndActivate);

// Host-side bindings: Ctrl+G greets, Ctrl+L opens the log.
app.UnhandledInput = input =>
{
    if (input.IsRawKey(Keys.Char('g'), Mods.Ctrl))
    {
        Log("greet (ctrl+g)");
        Greet();
        return true;
    }
    if (input.IsRawKey(Keys.Char('l'), Mods.Ctrl))
    {
        ShowEventLog();
        return true;
    }
    return false;
};
app.AltTap = () =>
{
    Log("alt tap");
    app.Announce("Alt: no menu in this demo.");
};

// ── Tickers ──

// Audio automation rides the ticker system.
var audioTicker = app.StartTicker(50);
audioTicker.Tick += audio.Tick;

// Once a minute, note the uptime.
var minutes = 0;
var uptime = app.StartTicker(60_000);
uptime.Tick += () => app.Announce($"Demo running for {++minutes} minute{(minutes == 1 ? "" : "s")}.");

app.Run();
return;

// ── Helpers ──

// Compose and queue the greeting from live widget state.
void Greet()
{
    var who = string.IsNullOrEmpty(name.Text) ? "stranger" : name.Text;
    var fruit = fruits.SelectedItem ?? "nothing";
    app.Announce(
        $"Hello, {who}. The fruit is {fruit}, and word wrap is {(wrap.Checked ? "on" : "off")}.");
}

// The event log as a reviewable status dialog, most recent first.
void ShowEventLog()
{
    var body = log.Count == 0 ? "No events yet." : string.Join("\n", log.AsEnumerable().Reverse());
    app.ShowStatus("Event log", body);
}

// A hand-built form dialog: prompt label, an edit box, Create/Cancel.
// Enter in the edit box falls through to Create (the layer's primary);
// Escape presses Cancel.
void OpenFormDialog()
{
    Log("form: opened");
    var dialog = app.OpenDialog();
    _ = new Label(dialog, "New playlist");
    var nameBox = new EditBox(dialog, "Name");
    var create = new Button(dialog, "Create");
    var cancel = new Button(dialog, "Cancel");
    create.Activated += () =>
    {
        var title = string.IsNullOrWhiteSpace(nameBox.Text) ? "Untitled" : nameBox.Text.Trim();
        dialog.Close();
        Log($"form: created \"{title}\"");
        app.Announce($"Created playlist {title}.");
    };
    cancel.Activated += () =>
    {
        dialog.Close();
        Log("form: cancelled");
        app.Announce("Cancelled.");
    };
    app.SetPrimary(create);
    app.SetCancel(cancel);
    dialog.AnnounceOpened();
}

// Two stacked layers. "Close both" closes the buried dialog directly,
// which takes the inner one with it (dialogs close strictly LIFO).
void OpenNestedDialog()
{
    Log("nested: outer opened");
    var outer = app.OpenDialog();
    _ = new Label(outer, "Outer dialog");
    var openInner = new Button(outer, "Open inner");
    var closeOuter = new Button(outer, "Close");
    openInner.Activated += () =>
    {
        Log("nested: inner opened");
        var inner = app.OpenDialog();
        _ = new Label(inner, "Inner dialog");
        var closeInner = new Button(inner, "Close inner");
        var closeBoth = new Button(inner, "Close both");
        closeInner.Activated += inner.Close;
        closeBoth.Activated += outer.Close;
        inner.Closed += () => Log("nested: inner closed");
        inner.AnnounceOpened();
    };
    closeOuter.Activated += outer.Close;
    outer.Closed += () => Log("nested: outer closed");
    outer.AnnounceOpened();
}

// Switch the SFX bus effect chain: dry, echo, or reverb.
void ApplyEffect(int index)
{
    switch (index)
    {
        case 0: // dry
            sfxBus.DisableDelay();
            sfxBus.DisableReverb();
            break;
        case 1: // echo
            sfxBus.DisableReverb();
            sfxBus.EnableDelay(delayMs: 220.0f, feedback: 0.4f, wet: 0.5f, dry: 0.9f);
            break;
        case 2: // reverb
            sfxBus.DisableDelay();
            sfxBus.EnableReverb(
                wet: 0.5f, dry: 0.9f, predelayMs: 20.0f, irGain: 1.0f, width: 1.0f,
                decay: 0.6f, lowcutHz: 120.0f, highcutHz: 9000.0f, diffuse: 0.5f);
            break;
    }
}

// A 300ms 880Hz ping with exponential decay, 16-bit mono WAV.
static void WritePingWav(string path)
{
    const int sampleRate = 44100;
    const int frames = sampleRate * 3 / 10;
    using var writer = new BinaryWriter(File.Create(path));
    writer.Write("RIFF"u8);
    writer.Write(36 + frames * 2);
    writer.Write("WAVE"u8);
    writer.Write("fmt "u8);
    writer.Write(16);
    writer.Write((short)1); // PCM
    writer.Write((short)1); // mono
    writer.Write(sampleRate);
    writer.Write(sampleRate * 2);
    writer.Write((short)2);
    writer.Write((short)16);
    writer.Write("data"u8);
    writer.Write(frames * 2);
    for (var i = 0; i < frames; i++)
    {
        var t = i / (float)sampleRate;
        var sample = MathF.Sin(2.0f * MathF.PI * 880.0f * t) * MathF.Exp(-8.0f * t) * 0.5f;
        writer.Write((short)(sample * short.MaxValue));
    }
}
