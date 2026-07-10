using Srui;

namespace SruiTasks;

/// <summary>A single-line edit box with console-style submit and recall:
/// Enter submits the trimmed text (raised through <see cref="Submitted"/>
/// at drain time) and clears the box; Up and Down walk previously
/// submitted entries, with the in-progress draft restored below the
/// newest. Recall replaces the content with SetTextSilently and speaks
/// just the recalled entry — the terse echo a full re-announcement would
/// bury. The base already claims plain Enter, Up, and Down (so
/// ReservesKey needs no override here); the subclass repurposes them
/// before the base call, trading away Enter's fall-through to the
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
            SetTextSilently("");
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
        if (_history.Count == 0)
        {
            AnnounceItem(CurrentSpoken(), null, Boundary.Top);
            return true;
        }
        if (_recall is not int index)
        {
            _draft = Text;
            Land(_history.Count - 1);
        }
        else if (index > 0)
        {
            Land(index - 1);
        }
        else
        {
            // Already at the oldest: re-announce in place.
            AnnounceItem(_history[0], null, Boundary.Top);
        }
        return true;
    }

    private bool RecallNewer()
    {
        if (_recall is not int index)
        {
            AnnounceItem(CurrentSpoken(), null, Boundary.Bottom);
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
            SetTextSilently(_draft);
            PostChanged();
            AnnounceItem(CurrentSpoken(), null, null);
        }
        return true;
    }

    private void Land(int index)
    {
        _recall = index;
        SetTextSilently(_history[index]);
        PostChanged();
        AnnounceItem(_history[index], null, null);
    }

    private string CurrentSpoken() => Text.Length == 0 ? "blank" : Text;
}
