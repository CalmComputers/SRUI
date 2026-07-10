using Srui;

namespace SruiTasks;

/// <summary>The application shell: root layout, view switching, global
/// shortcuts, and quit — the class that owns what the gallery demo does
/// in a top-level script. Behavior that belongs to a widget kind lives
/// in the widget subclasses; what remains here is genuinely application
/// policy.</summary>
public sealed class TaskApp
{
    private readonly TabControl _views;
    private readonly TasksPanel _tasks;
    private readonly StatsPanel _summary;

    public TaskApp(SruiApp app)
    {
        _ = new Label(app, "SRUI Tasks");
        _views = new TabControl(app, "Views", ["Tasks", "Summary"]);
        _tasks = new TasksPanel(app, Seed());
        _summary = new StatsPanel(app);
        // The control owns panel visibility: the active view shows, the
        // other hides and leaves the tab ring.
        _views.AttachPanels(_tasks, _summary);

        var quit = new ConfirmButton(app, "Quit", "Press again to quit.");
        quit.Activated += app.Quit;
        app.SetCancel(quit); // Escape presses Quit — twice to leave

        // Recompute the report when the Summary view is entered; the
        // panels have already settled when Changed fires.
        _views.Changed += () =>
        {
            if (_views.ActiveTab == "Summary")
                _summary.ShowReport(_tasks.List.Tasks);
        };

        // Alt+V jumps to the view switcher, Ctrl+N to the entry box;
        // Ctrl+Q presses Quit without moving focus (twice, like any
        // route into a ConfirmButton).
        _views.AddShortcut(KeyCombo.WithAlt(Key.Char('v')));
        _tasks.Entry.AddShortcut(KeyCombo.WithCtrl(Key.Char('n')));
        quit.AddShortcut(KeyCombo.WithCtrl(Key.Char('q')), ShortcutAction.Activate);
    }

    private static List<TaskItem> Seed() =>
    [
        new("Write the release notes"),
        new("Answer support mail") { Priority = Priority.High },
        new("Water the plants") { Priority = Priority.Low },
        new("Renew the domain") { Done = true },
    ];
}
