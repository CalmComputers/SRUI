using Srui;
using Xunit;

namespace Srui.Tests;

public class FocusIntrospectionTests
{
    [Fact]
    public void FocusedWidgetTracksFocus()
    {
        using var ui = new TestApp();
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
        using var ui = new TestApp();
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

    /// <summary>The ground was hidden whole while a dialog stood over
    /// it: the close has nowhere to restore to and says nothing, rather
    /// than reading the hidden widget as an arrival.</summary>
    [Fact]
    public void ClosingOverAHiddenGroundReadsNothing()
    {
        using var ui = new TestApp();
        var below = new Button(ui.App, "Below");
        below.Focus();
        ui.Drain();

        var dialog = ui.App.OpenDialog();
        var inside = new Button(dialog, "Inside");
        inside.Focus();
        ui.Drain();

        below.Hidden = true;
        dialog.Close();
        Assert.Empty(ui.Spoken());
        Assert.Null(ui.App.FocusedWidget);

        // Shown again, it is focused and read like any first focus.
        below.Hidden = false;
        ui.App.EnsureFocus();
        Assert.Equal(new[] { "Below button" }, ui.Spoken());
    }
}
