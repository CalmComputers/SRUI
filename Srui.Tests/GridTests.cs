using Srui;
using Xunit;

namespace Srui.Tests;

/// <summary>The grid: a cursor over cells that hold an item or
/// nothing, read as the item then the coordinate, the coordinate alone
/// on an empty cell, with the four edges, the corners, typeahead, and
/// a cell whose occupant changes under the cursor.</summary>
public class GridTests
{
    /// <summary>A 3 by 3 board with the middle empty:
    /// a b c / d . f / g h i.</summary>
    private static (TestApp Ui, Grid Grid) FocusedGrid(bool activateItems = false)
    {
        var ui = new TestApp();
        var grid = new Grid(ui.App, "Board", 3, 3, activateItems);
        var text = "abcdefghi";
        for (var row = 0; row < 3; row++)
            for (var column = 0; column < 3; column++)
                grid.SetCell(row, column, (row, column) == (1, 1) ? null : text[row * 3 + column].ToString());
        grid.Focus();
        ui.Drain();
        return (ui, grid);
    }

    [Fact]
    public void ArrivalReadsTheItemThenTheCoordinate()
    {
        var ui = new TestApp();
        var grid = new Grid(ui.App, "Board", 2, 2);
        grid.SetCell(0, 0, "x");
        grid.Focus();
        Assert.Equal(new[] { "Board grid x a1" }, ui.Spoken());
    }

    [Fact]
    public void MovesReadTheLandingAndEdgesAnnounce()
    {
        var (ui, _) = FocusedGrid();
        ui.Input(InputKind.MoveRight);
        Assert.Equal(new[] { "b b1" }, ui.Spoken());
        ui.Input(InputKind.MoveDown);
        Assert.Equal(new[] { "b2" }, ui.Spoken());          // the empty cell: coordinate alone
        ui.Input(InputKind.MoveDown);
        Assert.Equal(new[] { "h b3" }, ui.Spoken());
        ui.Input(InputKind.MoveDown);
        Assert.Equal(new[] { "bottom, h b3" }, ui.Spoken());
        ui.Input(InputKind.MoveLeft);
        ui.Input(InputKind.MoveLeft);
        Assert.Equal(new[] { "left, g a3" }, ui.Spoken());
    }

    [Fact]
    public void AnEdgeOnAnEmptyCellRereadsTheCoordinate()
    {
        var (ui, grid) = FocusedGrid();
        grid.Select(1, 1);
        ui.Drain();
        grid.SetCell(1, 0, null);
        ui.Drain();
        ui.Input(InputKind.MoveLeft);
        Assert.Equal(new[] { "a2" }, ui.Spoken());
        ui.Input(InputKind.MoveLeft);
        Assert.Equal(new[] { "left, a2" }, ui.Spoken());
    }

    [Fact]
    public void HomeEndAndCornersMove()
    {
        var (ui, grid) = FocusedGrid();
        grid.Select(1, 1);
        ui.Drain();
        ui.Input(InputKind.MoveToLineEnd);
        Assert.Equal(new[] { "f c2" }, ui.Spoken());
        ui.Input(InputKind.MoveToLineStart);
        Assert.Equal(new[] { "d a2" }, ui.Spoken());
        ui.Input(InputKind.MoveToDocEnd);
        Assert.Equal(new[] { "i c3" }, ui.Spoken());
        ui.Input(InputKind.MoveToDocStart);
        Assert.Equal(new[] { "a a1" }, ui.Spoken());
        ui.Input(InputKind.MoveToDocStart);
        Assert.Equal(new[] { "top, a a1" }, ui.Spoken());
    }

    [Fact]
    public void AChangedOccupantReadsUnderTheCursor()
    {
        var (ui, grid) = FocusedGrid();
        // The cursor did not move, so the place is not news.
        grid.SetCell(0, 0, "hole");
        Assert.Equal(new[] { "hole" }, ui.Spoken());
        grid.SetCell(0, 0, null);
        Assert.Equal(new[] { "a1" }, ui.Spoken());
    }

    [Fact]
    public void BoundCellsFollowTheModel()
    {
        var ui = new TestApp();
        var board = new[,] { { "1", "2" }, { "3", null } };
        var items = new Dictionary<string, ListItem>();
        var grid = new Grid(ui.App, null, 2, 2);
        grid.BindCells((row, column) => board[row, column] is { } text
            ? items.TryGetValue(text, out var item) ? item : items[text] = new ListItem(text)
            : null);
        grid.Focus();
        Assert.Equal(new[] { "grid 1 a1" }, ui.Spoken());
        // The model changes; the grid is never told.
        (board[0, 0], board[1, 1]) = (null, "1");
        Assert.Equal(new[] { "a1" }, ui.Spoken());
        Assert.Throws<InvalidOperationException>(() => grid[0, 0] = new ListItem("x"));
    }

    [Fact]
    public void TypeaheadHuntsInReadingOrderFromTheCursor()
    {
        var (ui, grid) = FocusedGrid();
        ui.Type("h");
        Assert.Equal(new[] { "h b3" }, ui.Spoken());
        Assert.Equal((2, 1), (grid.SelectedCell.Row, grid.SelectedCell.Column));
        ui.Wait(500);                                     // a fresh search, not the prefix "hc"
        ui.Type("c");
        Assert.Equal(new[] { "c c1" }, ui.Spoken());      // wrapped round
        ui.Wait(500);
        ui.Type("c");
        Assert.Equal(new[] { "c c1" }, ui.Spoken());      // the only bearer: still here
    }

    [Fact]
    public void EnterActivatesOnlyWhenAsked()
    {
        var (ui, grid) = FocusedGrid(activateItems: true);
        var activated = 0;
        grid.Activated += () => activated++;
        ui.Input(InputKind.Activate);
        Assert.Equal(1, activated);
        Assert.True(grid.ReservesKey(KeyCombo.Plain(Key.Enter)));

        var (_, plain) = FocusedGrid();
        Assert.False(plain.ReservesKey(KeyCombo.Plain(Key.Enter)));
        Assert.True(plain.ReservesKey(KeyCombo.WithCtrl(Key.Home)));
        Assert.False(plain.ReservesKey(KeyCombo.WithCtrl(Key.Up)));
    }

    [Fact]
    public void CoordinateSchemes()
    {
        Assert.Equal("a1", Grid.Alphanumeric(0, 0));
        Assert.Equal("z1", Grid.Alphanumeric(0, 25));
        Assert.Equal("aa1", Grid.Alphanumeric(0, 26));
        Assert.Equal("ab10", Grid.Alphanumeric(9, 27));
        Assert.Equal("row 3 column 2", Grid.Numeric(2, 1));

        var ui = new TestApp();
        var grid = new Grid(ui.App, null, 2, 2) { Coordinates = Grid.Numeric };
        grid.Focus();
        Assert.Equal(new[] { "grid row 1 column 1" }, ui.Spoken());
    }
}
