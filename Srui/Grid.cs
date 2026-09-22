namespace Srui;

/// <summary>A place in a grid: the row and column, zero-based from the
/// top left, and the name the grid's coordinate scheme gives it, which
/// is what speech says ("b3"). Readers that lay things out spatially
/// have the numbers.</summary>
public readonly record struct Cell(int Row, int Column, string Name);

/// <summary>How a grid names its cells. A joint scheme names a cell in
/// one breath (<see cref="Alphanumeric"/> "b3", <see cref="Numeric"/>
/// "3 2"); a scheme whose axes speak apart (<see cref="Tabular"/> "row 3
/// column 2") gives the grid a row field and a column field as well, so
/// a move reads only the axis it changed, and an arrival reads both.
/// Rows and columns are zero-based from the top left.</summary>
public sealed class CoordinateScheme
{
    private readonly Func<int, int, string> _name;
    private readonly Func<int, string>? _row;
    private readonly Func<int, string>? _column;

    private CoordinateScheme(Func<int, int, string> name, Func<int, string>? row, Func<int, string>? column)
    {
        _name = name;
        _row = row;
        _column = column;
    }

    /// <summary>A scheme that names a cell in one breath.</summary>
    public static CoordinateScheme Joint(Func<int, int, string> name) => new(name, null, null);

    /// <summary>A scheme whose axes speak apart: the cell's name is the
    /// row's then the column's, and a move reads only the one it
    /// changed.</summary>
    public static CoordinateScheme Apart(Func<int, string> row, Func<int, string> column) =>
        new((r, c) => $"{row(r)} {column(c)}", row, column);

    /// <summary>Column letter then row number: "a1", "b3"; columns past
    /// z go on "aa", "ab".</summary>
    public static readonly CoordinateScheme Alphanumeric =
        Joint(static (row, column) => $"{ColumnLetters(column)}{row + 1}");

    /// <summary>Row then column, as numbers: "3 2".</summary>
    public static readonly CoordinateScheme Numeric =
        Joint(static (row, column) => $"{row + 1} {column + 1}");

    /// <summary>"row 3 column 2", the axes apart: a spreadsheet's way,
    /// where a vertical move says the row and a horizontal one the
    /// column.</summary>
    public static readonly CoordinateScheme Tabular =
        Apart(static row => $"row {row + 1}", static column => $"column {column + 1}");

    /// <summary>The cell's name.</summary>
    public string Name(int row, int column) => _name(row, column);

    internal bool SpeaksApart => _row is not null;

    internal string RowName(int row) => _row!(row);

    internal string ColumnName(int column) => _column!(column);

    private static string ColumnLetters(int column)
    {
        var letters = "";
        for (var n = column; n >= 0; n = n / 26 - 1)
            letters = (char)('a' + n % 26) + letters;
        return letters;
    }
}

/// <summary>A two-dimensional list: rows and columns of cells, each
/// holding an item or nothing, under one cursor. Arrows move it with
/// boundary announcements at the four edges, Home/End go to the row's
/// ends, Ctrl+Home/Ctrl+End to the corners, printable characters hunt
/// by prefix over the items' lines in reading order from the cursor.
/// The cursor is a cell, not an item, so a cell's item can change under
/// it and the tick end reads the new occupant; landing on an empty cell
/// reads the coordinate alone, and on an item the item's fields then
/// the coordinate ("7 b3"). The coordinate's words are the grid's
/// <see cref="Coordinates"/> scheme (<see cref="CoordinateScheme"/>):
/// alphanumeric "b3" by default, numeric "3 2", tabular "row 3
/// column 2" whose axes speak apart so a move reads only the one it
/// changed, or the program's own.
/// By default Enter is not claimed and reaches the layer's primary;
/// <c>activateItems: true</c> claims it and raises
/// <see cref="Widget.Activated"/> for the cell under the cursor.
///
/// Cells are the grid's own (the indexer) or the program's
/// (<see cref="BindCells"/>: every read asks the source, so a board
/// that lives in the model needs no call to the grid when it moves).</summary>
public partial class Grid<T> : Widget where T : Element
{
    private const ulong TypeAheadTimeoutMs = 400;

    private readonly T?[] _cells;
    private Func<int, int, T?>? _source;
    private CoordinateScheme _coordinates = CoordinateScheme.Alphanumeric;
    private readonly bool _activateItems;
    private int _row;
    private int _column;
    private string _typeAheadBuffer = "";
    private ulong? _lastKeystrokeMs;

    /// <summary>Whether typed characters hunt by prefix (the default).
    /// Off, they fall through unclaimed, for a grid whose owner binds
    /// letters or Space itself.</summary>
    public bool Typeahead { get; set; } = true;

    public Grid(IWidgetContainer parent, string? name, int rows, int columns, bool activateItems = false)
        : base(parent, name, Role.Grid)
    {
        if (rows < 1)
            throw new ArgumentOutOfRangeException(nameof(rows));
        if (columns < 1)
            throw new ArgumentOutOfRangeException(nameof(columns));
        Rows = rows;
        Columns = columns;
        _cells = new T?[rows * columns];
        _activateItems = activateItems;
    }

    public int Rows { get; }

    public int Columns { get; }

    // ── Coordinates ──

    /// <summary>How a cell is named. <see cref="CoordinateScheme.Alphanumeric"/>
    /// unless set.</summary>
    public CoordinateScheme Coordinates
    {
        get => _coordinates;
        set
        {
            _coordinates = value;
            Touch();
        }
    }

    // ── Fields ──

    /// <summary>The cell under the cursor, named by the scheme.</summary>
    [Field] public Cell? Coordinate => new Cell(_row, _column, _coordinates.Name(_row, _column));

    /// <summary>The row's own name, under a scheme whose axes speak
    /// apart (<see cref="CoordinateScheme.Tabular"/>): then a move
    /// reads only the axis it changed. Absent under a joint scheme.</summary>
    [Field] public string? Row => _coordinates.SpeaksApart ? _coordinates.RowName(_row) : null;

    /// <summary>The column's own name; see <see cref="Row"/>.</summary>
    [Field] public string? Column => _coordinates.SpeaksApart ? _coordinates.ColumnName(_column) : null;

    protected internal override Element? CurrentItem => CellAt(_row, _column);

    // ── Cells ──

    /// <summary>The item in a cell, or null for an empty one. Setting
    /// replaces the stored cell; the cursor does not move, and reads the
    /// new occupant if it is there.</summary>
    public T? this[int row, int column]
    {
        get => CellAt(row, column);
        set
        {
            if (_source is not null)
                throw new InvalidOperationException("the cells are bound; change them at the source");
            _cells[Index(row, column)] = value;
            Touch();
        }
    }

    /// <summary>Make the program's model the grid's cells: every read
    /// asks the source, so the board follows the model with no call to
    /// the grid. The indexer's setter then throws.</summary>
    public void BindCells(Func<int, int, T?> source)
    {
        _source = source;
        Touch();
    }

    private T? CellAt(int row, int column) =>
        _source is { } source ? source(row, column) : _cells[Index(row, column)];

    private int Index(int row, int column)
    {
        if ((uint)row >= (uint)Rows)
            throw new ArgumentOutOfRangeException(nameof(row));
        if ((uint)column >= (uint)Columns)
            throw new ArgumentOutOfRangeException(nameof(column));
        return row * Columns + column;
    }

    // ── The cursor ──

    /// <summary>Where the cursor is. Setting moves it (clamped).</summary>
    public Cell SelectedCell
    {
        get => Coordinate!.Value;
        set => Select(value.Row, value.Column);
    }

    /// <summary>The item under the cursor, or null on an empty cell.</summary>
    public T? SelectedItem => CellAt(_row, _column);

    /// <summary>Move the cursor to a cell (clamped to the grid).</summary>
    public void Select(int row, int column)
    {
        _row = Math.Clamp(row, 0, Rows - 1);
        _column = Math.Clamp(column, 0, Columns - 1);
        Touch();
    }

    /// <summary>The cursor moved by input: the tick end reads the
    /// landing; notify the program.</summary>
    private void SelectAndNotify(int row, int column)
    {
        Select(row, column);
        PostChanged();
    }

    /// <summary>Move the cursor, or, when the move goes nowhere - off
    /// the grid, or Home at the row's start - say the edge and where the
    /// cursor still is.</summary>
    private bool Move(int row, int column, Boundary edge)
    {
        if ((uint)row >= (uint)Rows || (uint)column >= (uint)Columns
            || (row, column) == (_row, _column))
            AnnounceBoundary(edge);
        else
            SelectAndNotify(row, column);
        return true;
    }

    // ── Typeahead ──

    /// <summary>Forget any pending typeahead prefix - for a subclass
    /// whose own key changed the board (a slide), so the next
    /// keystroke starts a fresh search instead of extending a prefix
    /// typed against the old cells within the timeout.</summary>
    protected void ResetTypeahead()
    {
        _typeAheadBuffer = "";
        _lastKeystrokeMs = null;
    }

    private string TextOf(int row, int column) => CellAt(row, column)?.Get(Fields.Value) ?? "";

    private void HandleTypeAhead(string runeText)
    {
        var runeLower = AsciiMatch.LowerString(runeText);
        var now = NowMs;
        var shouldReset = _lastKeystrokeMs is not ulong last
            || now - Math.Min(now, last) > TypeAheadTimeoutMs;
        var cycling = _typeAheadBuffer.Length > 0 && AsciiMatch.IsRepeatsOf(_typeAheadBuffer, runeLower);
        if (shouldReset || cycling)
            _typeAheadBuffer = "";
        _typeAheadBuffer += runeLower;
        _lastKeystrokeMs = now;

        // A single letter cycles from the cell after the cursor; a
        // prefix searches from the cursor itself. Reading order, with
        // wraparound, as a list does.
        var single = cycling || _typeAheadBuffer == runeLower;
        var needle = single ? runeLower : _typeAheadBuffer;
        var count = Rows * Columns;
        var selected = _row * Columns + _column;
        for (var offset = single ? 1 : 0; offset <= count; offset++)
        {
            var at = (selected + offset) % count;
            var (row, column) = (at / Columns, at % Columns);
            if (!AsciiMatch.StartsWithLower(TextOf(row, column), needle))
                continue;
            if (at != selected)
                SelectAndNotify(row, column);
            else
                RereadItem();
            break;
        }
    }

    // ── Input ──

    public override bool ReservesKey(KeyCombo combo)
    {
        if (!combo.Alt && !combo.Shift)
        {
            if (combo.Key == Key.Home || combo.Key == Key.End)
                return true;
            if (combo.Ctrl)
                return base.ReservesKey(combo);
            if (combo.Key == Key.Up || combo.Key == Key.Down
                || combo.Key == Key.Left || combo.Key == Key.Right)
                return true;
            if (_activateItems && combo.Key == Key.Enter)
                return true;
            if (Typeahead && (combo.Key.IsChar(out _) || combo.Key == Key.Space))
                return true;
        }
        return base.ReservesKey(combo);
    }

    protected override bool OnInput(in InputEvent input)
    {
        switch (input.Kind)
        {
            case InputKind.MoveUp: return Move(_row - 1, _column, Boundary.Top);
            case InputKind.MoveDown: return Move(_row + 1, _column, Boundary.Bottom);
            case InputKind.MoveLeft: return Move(_row, _column - 1, Boundary.Left);
            case InputKind.MoveRight: return Move(_row, _column + 1, Boundary.Right);
            case InputKind.MoveToLineStart: return Move(_row, 0, Boundary.Left);
            case InputKind.MoveToLineEnd: return Move(_row, Columns - 1, Boundary.Right);
            case InputKind.MoveToDocStart: return Move(0, 0, Boundary.Top);
            case InputKind.MoveToDocEnd: return Move(Rows - 1, Columns - 1, Boundary.Bottom);
            case InputKind.Activate when _activateItems:
                PostActivated();
                return true;
            case InputKind.TypeChar when !Typeahead:
                return false;
            case InputKind.TypeChar:
                if (System.Text.Rune.IsValid((int)input.Ch))
                    HandleTypeAhead(char.ConvertFromUtf32((int)input.Ch));
                return true;
            default:
                return false;
        }
    }
}

/// <summary>The untyped grid — <see cref="Grid{T}"/> over
/// <see cref="ListItem"/> values, with plain strings as cells.</summary>
public class Grid : Grid<ListItem>
{
    public Grid(IWidgetContainer parent, string? name, int rows, int columns, bool activateItems = false)
        : base(parent, name, rows, columns, activateItems)
    {
    }

    /// <summary>Put plain text in a cell, or null to empty it.</summary>
    public void SetCell(int row, int column, string? text) =>
        this[row, column] = text is null ? null : new ListItem(text);
}
