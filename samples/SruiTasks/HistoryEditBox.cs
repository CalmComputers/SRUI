using Srui;

namespace SruiTasks;

/// <summary>A single-line edit box with console-style submit and recall:
/// Enter submits the trimmed text (raised through <see cref="Submitted"/>
/// at drain time) and clears the box; Up and Down walk previously
/// submitted entries, with the in-progress draft restored below the
/// newest. Recall replaces the content, and the tick end reads the new
/// line — the terse echo a full re-announcement would bury; an edge is
/// reported as a boundary. The base already claims plain Enter, Up, and
/// Down (so ReservesKey needs no override here); the subclass repurposes
/// them before the base call, trading away Enter's fall-through to the
/// layer's primary and the base's read-current-line Up and Down.
///
/// Editing a recalled entry edits the box only; the stored history is
/// immutable, and the draft is whatever was in the box when recall
/// began.</summary>
public class HistoryEditBox : EditBox
{
    private readonly List<string> _history = [];
    private int? _recall; // index into _history while recalling
    private string _draft = ""; // the box content when recall began

    public HistoryEditBox(IWidgetContainer parent, string? name) : base(parent, name)
    {
    }

    /// <summary>Enter was pressed: the trimmed text, possibly empty (the
    /// subscriber decides what an empty submit means).</summary>
    public event Action<string>? Submitted;

    protected override bool OnInput(in InputEvent input)
    {
        switch (input.Kind)
        {
            case InputKind.Activate:
                return Submit();
            case InputKind.MoveUp:
                return RecallOlder();
            case InputKind.MoveDown:
                return RecallNewer();
            default:
                return base.OnInput(input);
        }
    }

    private bool Submit()
    {
        var text = Text.Trim();
        if (text.Length != 0)
        {
            _history.Add(text);
            // The subscriber's outcome is the utterance; the box going
            // blank is not.
            Text = "";
            Suppress(Fields.Value);
            PostChanged();
        }
        _recall = null;
        _draft = "";
        // The subscriber speaks the outcome; capture the payload, post
        // the callback — handlers never run inside input dispatch.
        Post(() => Submitted?.Invoke(text));
        return true;
    }

    private bool RecallOlder()
    {
        if (_history.Count == 0 || _recall is 0)
        {
            // Nothing older: the edge, and the line again.
            AnnounceBoundary(Boundary.Top);
            Reread(Fields.Value);
            return true;
        }
        if (_recall is not int index)
        {
            _draft = Text;
            Land(_history.Count - 1);
        }
        else
        {
            Land(index - 1);
        }
        return true;
    }

    private bool RecallNewer()
    {
        if (_recall is not int index)
        {
            AnnounceBoundary(Boundary.Bottom);
            Reread(Fields.Value);
            return true;
        }
        if (index < _history.Count - 1)
        {
            Land(index + 1);
        }
        else
        {
            // Below the newest entry lies the draft.
            _recall = null;
            Text = _draft;
            PostChanged();
        }
        return true;
    }

    /// <summary>Replace the content with an entry; the tick end reads
    /// the line, and the cursor sits at its end, ready to edit.</summary>
    private void Land(int index)
    {
        _recall = index;
        Text = _history[index];
        CursorPosition = Text.Length;
        PostChanged();
    }
}
