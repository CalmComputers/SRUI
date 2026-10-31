using Srui;
using Xunit;

namespace Srui.Tests;

/// <summary>Grouped lists: the group read on arriving and on crossing
/// into another, not on every item; Right and Left going to the next and
/// previous group's first item, the edges said; and Up, Down, and
/// typeahead crossing groups as in any list.</summary>
public class GroupedListTests
{
    private static (TestApp Ui, GroupedListBox List) Focused()
    {
        var ui = new TestApp();
        var list = new GroupedListBox(ui.App, "Snippets",
        [
            new ListGroup("Shapes", ["sine", "saw", "square"]),
            new ListGroup("Filtering", ["dull", "brighten"]),
            new ListGroup("Phases", ["random phases"]),
        ], activateItems: true);
        list.Focus();
        return (ui, list);
    }

    [Fact]
    public void TheGroupIsReadOnArrivalAndOnCrossingOnly()
    {
        var (ui, _) = Focused();
        Assert.Equal(new[] { "Snippets list Shapes sine" }, ui.Spoken());

        ui.Input(InputKind.MoveDown);
        Assert.Equal(new[] { "saw" }, ui.Spoken());
        ui.Input(InputKind.MoveDown);
        ui.Spoken();
        ui.Input(InputKind.MoveDown);
        Assert.Equal(new[] { "Filtering dull" }, ui.Spoken());
        ui.Input(InputKind.MoveUp);
        Assert.Equal(new[] { "Shapes square" }, ui.Spoken());
    }

    [Fact]
    public void RightAndLeftGoByGroups()
    {
        var (ui, list) = Focused();
        ui.Spoken();
        ui.Input(InputKind.MoveDown);
        ui.Spoken();

        ui.Input(InputKind.MoveRight);
        Assert.Equal(new[] { "Filtering dull" }, ui.Spoken());
        ui.Input(InputKind.MoveRight);
        Assert.Equal(new[] { "Phases random phases" }, ui.Spoken());
        Assert.Equal(5, list.SelectedIndex);
        ui.Input(InputKind.MoveRight);
        Assert.Equal(5, list.SelectedIndex);
        Assert.NotEmpty(ui.Spoken());

        ui.Input(InputKind.MoveLeft);
        Assert.Equal(new[] { "Filtering dull" }, ui.Spoken());
        ui.Input(InputKind.MoveDown);
        ui.Spoken();
        // From inside a group, Left is the previous group's first item.
        ui.Input(InputKind.MoveLeft);
        Assert.Equal(new[] { "Shapes sine" }, ui.Spoken());
        ui.Input(InputKind.MoveLeft);
        Assert.Equal(0, list.SelectedIndex);
        Assert.NotEmpty(ui.Spoken());
    }

    [Fact]
    public void TypeaheadCrossesGroups()
    {
        var (ui, list) = Focused();
        ui.Spoken();
        ui.Type("b");
        Assert.Equal(new[] { "Filtering brighten" }, ui.Spoken());
        Assert.Equal(4, list.SelectedIndex);
    }
}
