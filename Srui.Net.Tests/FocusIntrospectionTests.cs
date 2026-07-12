using Srui;
using Xunit;

namespace Srui.Net.Tests;

public class FocusIntrospectionTests
{
    [Fact]
    public void FocusedWidgetTracksFocus()
    {
        using var ui = new TestUi();
        var first = new Button(ui.App, "First");
        var second = new Button(ui.App, "Second");
        Assert.Null(ui.App.FocusedWidget);

        first.Focus();
        Assert.Same(first, ui.App.FocusedWidget);

        second.Focus();
        Assert.Same(second, ui.App.FocusedWidget);
    }

    [Fact]
    public void FocusedWidgetFollowsDialogLayers()
    {
        using var ui = new TestUi();
        var below = new Button(ui.App, "Below");
        below.Focus();
        ui.Drain();

        var dialog = ui.App.OpenDialog();
        var inside = new Button(dialog, "Inside");
        inside.Focus();
        Assert.Same(inside, ui.App.FocusedWidget);

        dialog.Close();
        Assert.Same(below, ui.App.FocusedWidget);
    }
}
