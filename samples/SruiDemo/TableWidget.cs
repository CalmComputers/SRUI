using Srui;

namespace SruiDemo;

/// <summary>A two-dimensional table authored entirely from the public
/// Widget base, outside the toolkit assembly — the behavior-authoring
/// path. Arrows move the cell cursor; the fields are functions of it,
/// so the tick end reads a vertical move as the new cell with its row
/// position and a horizontal move as the column header with the cell,
/// and the user always knows which column they landed in. Home/End
/// jump within the row, edges announce without moving, and Enter
/// raises <see cref="RowActivated"/>. Because it overrides
/// <see cref="ReservesKey"/>, a bind dialog would warn about combos the
/// table swallows, exactly as for a built-in widget.</summary>
public partial class TableWidget : Widget
{
    private readonly string[] _columns;
    private readonly IReadOnlyList<string[]> _rows;
    private int _row;
    private int _col;

    public TableWidget(
        IWidgetContainer parent, string name, string[] columns, IReadOnlyList<string[]> rows)
        : base(parent, name, new Role("table"))
    {
        _columns = columns;
        _rows = rows;
    }

    public int Row => _row;

    public string Cell => _rows[_row][_col];

    // The fields are functions of the cell cursor: the framework reads
    // them at the tick end, so no announcing appears in OnInput. The
    // column header rides the value, so a horizontal move reads it.
    [Field] public string Value => $"{_columns[_col]}: {Cell}";

    /// <summary>The row position, spoken "N of M".</summary>
    [Field] public Position? Position => new(_row, _rows.Count);

    /// <summary>Enter on the table; the argument is the row index.</summary>
    public event Action<int>? RowActivated;

    public override bool ReservesKey(KeyCombo combo) =>
        !combo.Ctrl && !combo.Alt && !combo.Shift
        && (combo.Key == Key.Up || combo.Key == Key.Down
            || combo.Key == Key.Left || combo.Key == Key.Right
            || combo.Key == Key.Home || combo.Key == Key.End
            || combo.Key == Key.Enter);

    protected override bool OnInput(in InputEvent input)
    {
        switch (input.Kind)
        {
            case InputKind.MoveUp: return Move(_row - 1, _col, Boundary.Top);
            case InputKind.MoveDown: return Move(_row + 1, _col, Boundary.Bottom);
            case InputKind.MoveLeft: return Move(_row, _col - 1, Boundary.Left);
            case InputKind.MoveRight: return Move(_row, _col + 1, Boundary.Right);
            case InputKind.MoveToLineStart: return Move(_row, 0, Boundary.Left);
            case InputKind.MoveToLineEnd: return Move(_row, _columns.Length - 1, Boundary.Right);
            case InputKind.Activate:
                var row = _row;
                Post(() => RowActivated?.Invoke(row));
                return true;
            default:
                return false;
        }
    }

    private bool Move(int toRow, int toCol, Boundary edge)
    {
        var row = Math.Clamp(toRow, 0, _rows.Count - 1);
        var col = Math.Clamp(toCol, 0, _columns.Length - 1);
        if ((row, col) == (_row, _col))
        {
            // Nothing moved: the edge, and the cell again.
            AnnounceBoundary(edge);
            Reread(Fields.Value, Fields.Position);
            return true;
        }
        (_row, _col) = (row, col);
        Touch();
        PostChanged();
        return true;
    }
}
