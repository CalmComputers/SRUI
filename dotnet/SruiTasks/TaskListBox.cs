using Srui;

namespace SruiTasks;

public enum Priority
{
    Low = 0,
    Normal = 1,
    High = 2,
}

/// <summary>A to-do entry displayed by <see cref="TaskListBox"/> — an
/// <see cref="IListItem"/> whose spoken line is composed from its own
/// state. The list reads the line live, so mutating a task needs no
/// sync call — the handler just speaks the delta.</summary>
public sealed class TaskItem(string title) : IListItem
{
    public string Title { get; } = title;
    public Priority Priority { get; set; } = Priority.Normal;
    public bool Done { get; set; }

    /// <summary>The spoken line: the task, then whatever differs from the
    /// defaults ("Answer support mail, high priority, done").</summary>
    public string Text
    {
        get
        {
            var text = Title;
            if (Priority == Priority.Low)
                text += ", low priority";
            else if (Priority == Priority.High)
                text += ", high priority";
            if (Done)
                text += ", done";
            return text;
        }
    }
}

/// <summary>A ListBox whose items are mutable tasks: Space toggles done,
/// Delete removes, Shift+Up/Down reorders, and Left/Right sets priority;
/// arrows, Home/End, and type-ahead are inherited. This is the subclass
/// contract for state-bearing lists: the items themselves are the state
/// (TaskItem composes its spoken line, which the list reads live), so a
/// handler mutates the task or removes with RemoveAt, and owns only the
/// editorial announcement — terse and value-only, exactly how the
/// built-in speaks; the structural consequence (where the selection
/// lands) is the base's. Declaring the claimed combos in ReservesKey
/// keeps bind-dialog conflict warnings accurate for this subclass too.
///
/// Claiming Space costs multi-word type-ahead ("water t" would toggle at
/// the space) — the kind of tradeoff every key-claiming subclass makes;
/// letter cycling and single-word prefixes still work.</summary>
public class TaskListBox : ListBox
{
    public TaskListBox(IWidgetContainer parent, string name, IEnumerable<TaskItem> tasks)
        : base(parent, name, tasks.ToList(), numbered: true)
    {
    }

    /// <summary>The tasks, in list order — the base's items, typed.</summary>
    public IEnumerable<TaskItem> Tasks => Items.Cast<TaskItem>();

    private TaskItem Selected => (TaskItem)Items[SelectedIndex];

    /// <summary>Remove every done task; returns how many went.</summary>
    public int ClearCompleted()
    {
        var keep = Items.Where(t => !((TaskItem)t).Done).ToList();
        var removed = Items.Count - keep.Count;
        if (removed > 0)
            SetItems(keep);
        return removed;
    }

    public override bool ReservesKey(KeyCombo combo)
    {
        if (!combo.Ctrl && !combo.Alt && !combo.Shift
            && (combo.Key == Key.Delete || combo.Key == Key.Left || combo.Key == Key.Right))
            return true;
        if (combo.Shift && !combo.Ctrl && !combo.Alt
            && (combo.Key == Key.Up || combo.Key == Key.Down))
            return true;
        return base.ReservesKey(combo);
    }

    protected override bool OnInput(in InputEvent input)
    {
        if (Items.Count == 0)
            return base.OnInput(input);
        if (input.IsChar(' '))
            return ToggleDone();
        if (input.Is(Key.Delete))
            return RemoveSelected();
        if (input.Is(KeyCombo.WithShift(Key.Up)))
            return Reorder(-1);
        if (input.Is(KeyCombo.WithShift(Key.Down)))
            return Reorder(+1);
        if (input.Is(Key.Left))
            return StepPriority(-1);
        if (input.Is(Key.Right))
            return StepPriority(+1);
        return base.OnInput(input);
    }

    private bool ToggleDone()
    {
        var task = Selected;
        task.Done = !task.Done;
        // The item's Text now carries ", done"; the list reads it live,
        // so only the delta needs speaking.
        Announce(task.Done ? "Done." : "Not done.");
        PostChanged();
        return true;
    }

    private bool RemoveSelected()
    {
        // Editorial first; RemoveAt speaks where the selection lands (a
        // survivor with its position, or "empty").
        Announce($"Deleted {Selected.Title}.");
        RemoveAt(SelectedIndex);
        PostChanged();
        return true;
    }

    private bool Reorder(int direction)
    {
        var from = SelectedIndex;
        var to = from + direction;
        if (to < 0 || to >= Items.Count)
        {
            // At the edge: re-announce in place with the boundary, the
            // same vocabulary list navigation uses.
            AnnounceItem(Items[from].Text, (from, Items.Count),
                direction < 0 ? Boundary.Top : Boundary.Bottom);
            return true;
        }
        var moving = Items[from];
        SetItem(from, Items[to]);
        SetItem(to, moving);
        // The public setter shares the user-driven emission: the item
        // with its new position ("Water the plants, 2 of 4").
        SelectedIndex = to;
        PostChanged();
        return true;
    }

    private bool StepPriority(int direction)
    {
        var task = Selected;
        var target = (Priority)Math.Clamp((int)task.Priority + direction, 0, (int)Priority.High);
        var moved = target != task.Priority;
        task.Priority = target;
        // Announce even when clamped at an edge, like a slider.
        Announce($"{PriorityWord(target)} priority.");
        if (moved)
            PostChanged();
        return true;
    }

    private static string PriorityWord(Priority priority) => priority switch
    {
        Priority.Low => "Low",
        Priority.High => "High",
        _ => "Normal",
    };
}
