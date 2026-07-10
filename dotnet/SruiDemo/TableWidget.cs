using Srui;

namespace SruiDemo;

/// <summary>A two-dimensional table authored entirely from the public
/// Widget base, outside the toolkit assembly — the behavior-authoring
/// path. Arrows move the cell cursor: vertical moves speak the new cell
/// with its row position, horizontal moves speak the column header with
/// the cell so the user always knows which column they landed in.
/// Home/End jump within the row, edges announce without moving, and
/// Enter raises <see cref="RowActivated"/>. Because it overrides
/// <see cref="ReservesKey"/>, a bind dialog would warn about combos the
/// table swallows, exactly as for a built-in widget.</summary>
public class TableWidget : Widget
{
    private readonly string[] _columns;
    private readonly IReadOnlyList<string[]> _rows;
    private int _row;
    private int _col;

    public TableWidget(
        IWidgetContainer parent, string name, string[] columns, IReadOnlyList<string[]> rows)
        : base(parent, name, roleText: "table")
    {
        _columns = columns;
        _rows = rows;
        SetValue($"{_columns[_col]}: {Cell}");
        SetStateText($"row {_row + 1} of {_rows.Count}");
    }

    public int Row => _row;

    public string Cell => _rows[_row][_col];

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
            case InputKind.MoveUp: return Move(_row - 1, _col, vertical: true);
            case InputKind.MoveDown: return Move(_row + 1, _col, vertical: true);
            case InputKind.MoveLeft: return Move(_row, _col - 1, vertical: false);
            case InputKind.MoveRight: return Move(_row, _col + 1, vertical: false);
            case InputKind.MoveToLineStart: return Move(_row, 0, vertical: false);
            case InputKind.MoveToLineEnd: return Move(_row, _columns.Length - 1, vertical: false);
            case InputKind.Activate:
                var row = _row;
                Post(() => RowActivated?.Invoke(row));
                return true;
            default:
                return false;
        }
    }

    private bool Move(int toRow, int toCol, bool vertical)
    {
        var row = Math.Clamp(toRow, 0, _rows.Count - 1);
        var col = Math.Clamp(toCol, 0, _columns.Length - 1);
        var moved = (row, col) != (_row, _col);
        (_row, _col) = (row, col);
        SetValue($"{_columns[_col]}: {Cell}");
        SetStateText($"row {_row + 1} of {_rows.Count}");

        if (vertical)
        {
            // The column is unchanged, so the cell alone orients; the
            // row position rides along, boundary edges announce in place.
            Boundary? boundary = moved ? null : toRow < row ? Boundary.Top : Boundary.Bottom;
            AnnounceItem(Cell, (_row, _rows.Count), boundary);
        }
        else
        {
            // Landing in a new column: lead with its header.
            AnnounceItem(moved ? $"{_columns[_col]}: {Cell}" : $"edge, {_columns[_col]}: {Cell}",
                null, null);
        }
        if (moved)
            PostChanged();
        return true;
    }
}
