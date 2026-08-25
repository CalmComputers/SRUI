namespace Srui.Core;

/// <summary>Multi-level undo history for <see cref="EditorState"/>. A
/// unit is the list of primitive splices one operation or one typing
/// sequence performed, with the cursor and selection captured on both
/// sides, so undoing restores exactly where the user stood before it —
/// a selection delete comes back selected. Recording happens inside
/// operation scopes (<see cref="BeginOp"/>/<see cref="EndOp"/>): a
/// typing sequence keeps its unit open and grows it, and the sequence
/// ends only when the cursor is not where the last edit left it as the
/// next edit arrives, when <see cref="MergeWindowMs"/> passes without
/// an edit (checked lazily at the next edit — no timer), or when a bulk
/// operation (paste, cut, a selection delete or replace, a word delete,
/// programmatic editing) takes a unit of its own. Memory is bounded by
/// <see cref="MaxChars"/>; the newest unit is always kept.</summary>
internal sealed class UndoHistory
{
    /// <summary>A typing sequence ends when this much time passes
    /// between edits.</summary>
    public const ulong MergeWindowMs = 10_000;

    /// <summary>One recorded splice: [Start, Start + Removed.Length)
    /// became Inserted. Positions are in the document as it stood when
    /// the splice ran, so a unit replays forward in order and reverses
    /// in reverse order.</summary>
    public readonly record struct Splice(int Start, string Removed, string Inserted);

    /// <summary>One undo step.</summary>
    public sealed class Unit
    {
        public readonly List<Splice> Splices = new();
        public int CursorBefore;
        public (int Anchor, int Cursor)? SelectionBefore;
        public int CursorAfter;
        public (int Anchor, int Cursor)? SelectionAfter;
        /// <summary>Clock reading at the unit's most recent edit —
        /// the timeout side of the sequence-merge check.</summary>
        public ulong LastEditMs;
    }

    /// <summary>The engine clock, fed by the edit box as it handles
    /// input. Drives the typing-sequence timeout; never read except at
    /// the next edit.</summary>
    public ulong Now;

    /// <summary>Retained-character budget: UTF-16 code units of removed
    /// plus inserted text summed over every held unit. Past it, the
    /// oldest undo units are evicted — the newest always stays, even
    /// alone over budget. Checked as units close, so a smaller budget
    /// applies from the next edit.</summary>
    public int MaxChars = 2_000_000;

    // Stacks, top at the end. _open is the typing sequence still
    // accepting edits — always the top undo unit; null when the top is
    // sealed (a bulk unit, or anything popped by undo/redo).
    private readonly List<Unit> _undo = new();
    private readonly List<Unit> _redo = new();
    private Unit? _open;
    private int _chars;

    // The operation scope in flight. Scopes nest (a selection delete
    // inside an insert); only the outermost one decides and records.
    private int _depth;
    private bool _bulk;
    private int _cursorBefore;
    private (int Anchor, int Cursor)? _selectionBefore;
    /// <summary>The unit this op appends to: the open sequence when the
    /// op merges, else created lazily by the first splice — an op that
    /// mutates nothing leaves no trace.</summary>
    private Unit? _target;

    public bool CanUndo => _undo.Count != 0;
    public bool CanRedo => _redo.Count != 0;

    /// <summary>Enter an operation scope. Bulk operations never merge
    /// into the open typing sequence and seal their own unit at
    /// <see cref="EndOp"/>; non-bulk edits continue the sequence when
    /// the cursor is where its last edit left it and the window has not
    /// elapsed.</summary>
    public void BeginOp(int cursor, (int Anchor, int Cursor)? selection, bool bulk)
    {
        if (_depth++ != 0)
            return;
        _bulk = bulk;
        _cursorBefore = cursor;
        _selectionBefore = selection;
        _target = !bulk && _open is not null
            && cursor == _open.CursorAfter
            && Now - _open.LastEditMs <= MergeWindowMs
            ? _open
            : null;
    }

    /// <summary>Record one splice into the current operation's unit.
    /// Throws outside a scope — the guard that keeps every future edit
    /// path recorded rather than silently unrecorded.</summary>
    public void RecordSplice(int start, string removed, string inserted)
    {
        if (_depth == 0)
            throw new InvalidOperationException("Splice outside an edit operation scope.");
        if (_redo.Count != 0)
            ClearRedo();
        if (_target is null)
        {
            _target = new Unit { CursorBefore = _cursorBefore, SelectionBefore = _selectionBefore };
            _undo.Add(_target);
        }
        _target.Splices.Add(new Splice(start, removed, inserted));
        _chars += removed.Length + inserted.Length;
    }

    /// <summary>Leave an operation scope, stamping the after-state and
    /// the clock on the touched unit and evicting past the budget.</summary>
    public void EndOp(int cursor, (int Anchor, int Cursor)? selection)
    {
        if (--_depth != 0 || _target is null)
            return;
        _target.CursorAfter = cursor;
        _target.SelectionAfter = selection;
        _target.LastEditMs = Now;
        _open = _bulk ? null : _target;
        _target = null;
        while (_chars > MaxChars && _undo.Count > 1)
        {
            _chars -= Chars(_undo[0]);
            _undo.RemoveAt(0);
        }
    }

    /// <summary>Take the top undo unit, moving it to the redo stack and
    /// sealing the typing sequence. Null with nothing to undo.</summary>
    public Unit? PopUndo()
    {
        _open = null;
        if (_undo.Count == 0)
            return null;
        var unit = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(unit);
        return unit;
    }

    /// <summary>Take the top redo unit, moving it back to the undo
    /// stack, sealed. Null with nothing to redo.</summary>
    public Unit? PopRedo()
    {
        if (_redo.Count == 0)
            return null;
        var unit = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(unit);
        return unit;
    }

    /// <summary>Drop everything — the content was replaced wholesale
    /// (a document load), so history would undo across documents.</summary>
    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        _open = null;
        _target = null;
        _chars = 0;
    }

    private void ClearRedo()
    {
        foreach (var unit in _redo)
            _chars -= Chars(unit);
        _redo.Clear();
    }

    private static int Chars(Unit unit)
    {
        var total = 0;
        foreach (var splice in unit.Splices)
            total += splice.Removed.Length + splice.Inserted.Length;
        return total;
    }
}
