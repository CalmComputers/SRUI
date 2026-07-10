using System.Text;
using Srui;

namespace SruiTasks;

/// <summary>The Tasks view: entry box, task list, and the clear button,
/// built and wired inside the component — the composite-widget pattern.
/// A Group subclass is a full container: children created with
/// <c>this</c> as their parent land inside it, and the protected Widget
/// surface (Announce, here) carries the component's own feedback. The
/// wiring between the children is the component's business; only the
/// widgets themselves are exposed, for the shell's shortcuts.</summary>
public sealed class TasksPanel : Group
{
    public HistoryEditBox Entry { get; }
    public TaskListBox List { get; }
    public ConfirmButton Clear { get; }

    public TasksPanel(IWidgetContainer parent, IEnumerable<TaskItem> seed)
        : base(parent, "Tasks")
    {
        Entry = new HistoryEditBox(this, "New task")
        {
            Description = "Enter adds the task. Up and down recall earlier entries.",
        };
        List = new TaskListBox(this, "To-do", seed)
        {
            Description = "Space marks done, Delete removes, shift with arrows "
                + "reorders, left and right set priority.",
        };
        Clear = new ConfirmButton(this, "Clear completed",
            "Press again to clear every done task.");

        Entry.Submitted += text =>
        {
            if (text.Length == 0)
            {
                Announce("Type a task first.");
                return;
            }
            List.Add(new TaskItem(text));
            Announce($"Added {text}.");
        };
        Clear.Activated += () =>
        {
            var removed = List.ClearCompleted();
            Announce(removed == 0
                ? "Nothing to clear."
                : $"Cleared {removed} done task{(removed == 1 ? "" : "s")}.");
        };
    }
}

/// <summary>The Summary view: a read-only report over the task list,
/// recomputed by the shell each time the view is entered. Reviewable
/// like any read-only edit box: arrows, words, selection, copy.</summary>
public sealed class StatsPanel : Group
{
    private readonly EditBox _report;

    public StatsPanel(IWidgetContainer parent) : base(parent, "Summary")
    {
        _report = new EditBox(this, "Report", multiline: true) { ReadOnly = true };
    }

    public void ShowReport(IEnumerable<TaskItem> tasks)
    {
        // The panel is hidden (or at least unfocused) while the tab
        // control changes views, so the Text setter stays silent.
        _report.Text = Compose(tasks.ToList());
    }

    private static string Compose(IReadOnlyList<TaskItem> tasks)
    {
        if (tasks.Count == 0)
            return "No tasks.";
        var open = tasks.Where(t => !t.Done).ToList();
        var report = new StringBuilder();
        report.Append($"{tasks.Count} task{(tasks.Count == 1 ? "" : "s")}: ")
            .Append($"{open.Count} open, {tasks.Count - open.Count} done.");
        foreach (var priority in new[] { Priority.High, Priority.Normal, Priority.Low })
        {
            var group = open.Where(t => t.Priority == priority).Select(t => t.Title).ToList();
            if (group.Count != 0)
                report.Append('\n').Append($"{priority}: {string.Join("; ", group)}.");
        }
        return report.ToString();
    }
}
