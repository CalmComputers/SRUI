using Srui;
using Xunit;

namespace Srui.Tests;

public class InputFilterTests
{
    [Fact]
    public void FilterConsumesAheadOfEveryClaim()
    {
        using var ui = new TestApp();
        var button = new Button(ui.App, "Go");
        bool activated = false;
        button.Activated += () => activated = true;
        button.Focus();
        ui.Drain();

        var seen = new List<InputKind>();
        ui.App.InputFilter = input =>
        {
            seen.Add(input.Kind);
            return true;
        };
        bool unhandledRan = false;
        ui.App.UnhandledInput = _ => unhandledRan = true;

        Assert.True(ui.Input(InputKind.Activate));
        ui.Drain();
        Assert.False(activated);
        Assert.False(unhandledRan);
        Assert.Equal([InputKind.Activate], seen);
    }

    [Fact]
    public void DecliningFilterLetsInputFlow()
    {
        using var ui = new TestApp();
        var button = new Button(ui.App, "Go");
        bool activated = false;
        button.Activated += () => activated = true;
        button.Focus();
        ui.Drain();

        ui.App.InputFilter = _ => false;
        Assert.True(ui.Input(InputKind.Activate));
        ui.Drain();
        Assert.True(activated);
    }

    [Fact]
    public void FilterSeesDialogInputToo()
    {
        using var ui = new TestApp();
        var below = new Button(ui.App, "Below");
        below.Focus();
        ui.Drain();

        var dialog = ui.App.OpenDialog();
        var inside = new Button(dialog, "Inside");
        bool insideActivated = false;
        inside.Activated += () => insideActivated = true;
        inside.Focus();
        ui.Drain();

        // A filter that stands down behind an open dialog: the
        // documented HasOpenDialog idiom.
        ui.App.InputFilter = _ => !ui.App.HasOpenDialog;
        Assert.True(ui.Input(InputKind.Activate));
        ui.Drain();
        Assert.True(insideActivated);
    }
}
