// Four apps in one window under a MultiAppHost: ctrl+tab and
// ctrl+shift+tab cycle (speaking the app name), and Home's task list
// switches directly (FocusOnly — you just chose the name, so it isn't
// repeated). Notes sends messages to Inbox (the messaging path); Inbox
// announces arrivals from any app (AnnouncesInBackground); Stopwatch
// keeps running while backgrounded but its periodic announcements stay
// private to it (no flag), and it can close itself.

using MultiAppDemo;
using Srui;

using var host = new MultiAppHost("SRUI Multi-App Demo");

// ── Home: the hub — a live task list; Enter switches to the picked
// app without re-announcing its name ──

var home = host.Add("Home");
var taskList = new ListBox<AppTask>(
    home.App, "Tasks", Array.Empty<AppTask>(), numbered: true, activateItems: true);
taskList.Activated += () =>
{
    if (taskList.SelectedItem is { } task)
        host.Activate(task.Hosted, SwitchAnnouncement.FocusOnly);
};

// ── Notes: compose a note, send it to Inbox ──

var notes = host.Add("Notes");
var noteBox = new EditBox(notes.App, "Note", multiline: true);
var send = new Button(notes.App, "Send to Inbox");
notes.App.SetPrimary(send);

// ── Stopwatch: runs whether or not it is the active app ──

var stopwatch = host.Add("Stopwatch");
var running = false;
ulong startedAt = 0;
ulong banked = 0;
ulong ElapsedMs() => banked + (running ? stopwatch.App.Now - startedAt : 0);

var toggle = new Button(stopwatch.App, "Start");
_ = new ElapsedWidget(stopwatch.App, ElapsedMs);
toggle.Activated += () =>
{
    if (running)
    {
        banked += stopwatch.App.Now - startedAt;
        running = false;
        toggle.Name = "Start";
    }
    else
    {
        startedAt = stopwatch.App.Now;
        running = true;
        toggle.Name = "Stop";
    }
};

// No AnnouncesInBackground on this app, so only the Stopwatch app
// itself hears the periodic tick — switch to Notes and it goes quiet
// while the time keeps accumulating.
var ticker = stopwatch.App.StartTicker(10_000);
ticker.Tick += () =>
{
    if (running)
        stopwatch.App.Announce($"{ElapsedMs() / 1000} seconds");
};

// Closing: the neighbor app is activated and announced, and ctrl+tab
// no longer visits the stopwatch. Activated handlers run at drain
// time, so closing from one is the documented-safe path.
var closeStopwatch = new Button(stopwatch.App, "Close Stopwatch");
closeStopwatch.Activated += () => stopwatch.Close();

// ── Inbox: receives messages from anywhere, heard from anywhere ──

var inbox = host.Add("Inbox");
inbox.AnnouncesInBackground = true;
var messages = new ListBox(inbox.App, "Messages", Array.Empty<string>());
inbox.MessageReceived += message =>
{
    if (message is NoteMessage(var text))
    {
        messages.Add(text);
        inbox.App.Announce($"New message: {text}");
    }
};

send.Activated += () =>
{
    var text = noteBox.Text.Trim();
    if (text.Length == 0)
    {
        notes.App.Announce("Nothing to send");
        return;
    }
    inbox.Send(new NoteMessage(text));
    noteBox.Text = "";
    notes.App.Announce("Sent");
};

// The task list tracks the app set live: names are read at
// announcement time, and AppsChanged covers closes (the stopwatch)
// and any future additions.
void RefreshTasks() => taskList.Items = host.Apps
    .Where(hosted => !ReferenceEquals(hosted, home))
    .Select(hosted => new AppTask(hosted))
    .ToList();
host.AppsChanged += RefreshTasks;
RefreshTasks();

host.Run();

namespace MultiAppDemo
{
    /// <summary>What Notes sends and Inbox receives.</summary>
    public sealed record NoteMessage(string Text);

    /// <summary>A task-list entry: the line is the hosted app's name,
    /// read live, so a renamed app needs no list refresh.</summary>
    public sealed class AppTask : IListItem
    {
        public HostedApp Hosted { get; }

        public AppTask(HostedApp hosted) => Hosted = hosted;

        public string Text => Hosted.Name;
    }

    /// <summary>Read-only elapsed display: the value is computed when an
    /// announcement needs it, never stored.</summary>
    public sealed class ElapsedWidget : CustomWidget
    {
        private readonly Func<ulong> _elapsedMs;

        public ElapsedWidget(IWidgetContainer parent, Func<ulong> elapsedMs)
            : base(parent, "Elapsed") => _elapsedMs = elapsedMs;

        protected override string ValueText => $"{_elapsedMs() / 1000} seconds";
    }
}
