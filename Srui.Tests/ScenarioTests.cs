using Srui;
using Xunit;

namespace Srui.Tests;

public class ScenarioRunTests
{
    /// <summary>Two buttons: One announces on activation, Quiet does
    /// nothing. Startup speech is "One button".</summary>
    private static TestApp Demo() => new(app =>
    {
        var one = new Button(app, "One");
        one.Activated += () => app.Announce("fired one");
        _ = new Button(app, "Quiet");
    });

    [Fact]
    public void HappyPathAssertsBatchesInOrder()
    {
        using var ui = Demo();
        ui.RunScenarioText("""
            // startup, then activate, then move on
            say One button
            enter
            say fired one
            tab
            say Quiet button
            """);
    }

    [Fact]
    public void UnassertedBatchesAreFlushedAndIgnored()
    {
        using var ui = Demo();
        // Startup speech and the activation announcement both go
        // unasserted; only the final focus change is in scope.
        ui.RunScenarioText("""
            enter
            tab
            say Quiet button
            """);
    }

    [Fact]
    public void SayMismatchReportsSourceAndLine()
    {
        using var ui = Demo();
        var ex = Assert.Throws<ScenarioException>(() =>
            ui.RunScenarioText("say Wrong button", "startup.srs"));
        Assert.Contains("startup.srs:1:", ex.Message);
        Assert.Contains("\"One button\"", ex.Message);
    }

    [Fact]
    public void PartiallyAssertedBatchFails()
    {
        using var ui = new TestApp(app =>
        {
            var both = new Button(app, "Both");
            both.Activated += () =>
            {
                app.Announce("first");
                app.Announce("second");
            };
        });
        var ex = Assert.Throws<ScenarioException>(() =>
            ui.RunScenarioText("""
                enter
                say first
                tab
                """));
        Assert.Contains("\"second\"", ex.Message);
    }

    [Fact]
    public void PartiallyAssertedFinalBatchFails()
    {
        using var ui = Demo();
        Assert.Throws<ScenarioException>(() =>
            ui.RunScenarioText("""
                enter
                tab
                say Quiet
                """));
    }

    [Fact]
    public void NoSpeechAssertsSilence()
    {
        using var ui = Demo();
        ui.RunScenarioText("""
            tab
            enter
            nospeech
            """);
    }

    [Fact]
    public void NoSpeechFailsWhenSomethingWasSaid()
    {
        using var ui = Demo();
        var ex = Assert.Throws<ScenarioException>(() =>
            ui.RunScenarioText("""
                enter
                nospeech
                """, "quiet.srs"));
        Assert.Contains("quiet.srs:2:", ex.Message);
        Assert.Contains("\"fired one\"", ex.Message);
    }

    [Fact]
    public void TypeVerbTypesVerbatim()
    {
        EditBox edit = null!;
        using var ui = new TestApp(app => edit = new EditBox(app, "Name", ""));
        ui.RunScenarioText("type hello world");
        Assert.Equal("hello world", edit.Text);
    }

    [Fact]
    public void WaitVerbElapsesTime()
    {
        using var ui = new TestApp(app =>
        {
            _ = new Button(app, "One");
            app.StartTicker(100).Tick += () => app.Announce("tick");
        });
        ui.RunScenarioText("""
            wait 150
            say tick
            """);
    }

    [Fact]
    public void DownAndUpVerbsDriveThePhysicalStream()
    {
        using var ui = new TestApp(app =>
        {
            var pad = new Button(app, "Pad");
            pad.BindKey(KeyCombo.Plain(Key.Char('j')), KeyPhase.Press,
                () => app.Announce("held"));
            pad.BindKey(KeyCombo.Plain(Key.Char('j')), KeyPhase.Release,
                () => app.Announce("released"));
        });
        ui.RunScenarioText("""
            down j
            say held
            up j
            say released
            """);
    }

    [Fact]
    public void BareDownWordIsTheArrowKey()
    {
        ListBox list = null!;
        using var ui = new TestApp(app =>
            list = new ListBox(app, "Items", ["alpha", "beta"]));
        ui.RunScenarioText("down");
        Assert.Equal(1, list.SelectedIndex);
    }

    [Theory]
    [InlineData("say")]
    [InlineData("wait soon")]
    [InlineData("not a combo")]
    [InlineData("down q+j")]
    public void MalformedLinesReportSourceAndLine(string line)
    {
        using var ui = Demo();
        var ex = Assert.Throws<ScenarioException>(() =>
            ui.RunScenarioText(line, "bad.srs"));
        Assert.Contains("bad.srs:1:", ex.Message);
    }
}

public class ScenarioRecordTests
{
    private static TestApp Demo() => new(app =>
    {
        var one = new Button(app, "One");
        one.Activated += () => app.Announce("fired one");
        _ = new Button(app, "Quiet");
    });

    [Fact]
    public void RecorderFillsAssertionsFromTheRun()
    {
        using var ui = Demo();
        var recorded = ui.RecordScenarioText("""
            // the exchange
            enter
            tab
            """);
        Assert.Equal("""
            // the exchange
            say One button
            enter
            say fired one
            tab
            say Quiet button

            """.ReplaceLineEndings("\n"), recorded);
    }

    [Fact]
    public void RecorderFreezesSilenceAsNoSpeech()
    {
        using var ui = Demo();
        var recorded = ui.RecordScenarioText("""
            tab
            enter
            """);
        Assert.Contains("enter\nnospeech\n", recorded);
    }

    [Fact]
    public void RecorderReplacesStaleAssertions()
    {
        using var ui = Demo();
        var recorded = ui.RecordScenarioText("""
            say Stale startup
            enter
            say stale utterance
            nospeech
            """);
        Assert.DoesNotContain("Stale", recorded);
        Assert.DoesNotContain("stale", recorded);
        Assert.Contains("say fired one", recorded);
    }

    [Fact]
    public void RecordEnvironmentVariableTurnsRunIntoRecord()
    {
        var path = Path.Combine(Path.GetTempPath(), $"srui-record-{Guid.NewGuid():N}.srs");
        File.WriteAllText(path, "enter\ntab\n");
        try
        {
            Environment.SetEnvironmentVariable("SRUI_RECORD", "1");
            try
            {
                using var recordingUi = Demo();
                recordingUi.RunScenario(path);
            }
            finally
            {
                Environment.SetEnvironmentVariable("SRUI_RECORD", null);
            }
            var recorded = File.ReadAllText(path);
            Assert.Contains("say fired one", recorded);

            // With the variable cleared, the same call replays and holds.
            using var replayUi = Demo();
            replayUi.RunScenario(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RecordedScenarioReplaysCleanly()
    {
        string recorded;
        using (var recordingUi = Demo())
            recorded = recordingUi.RecordScenarioText("""
                enter
                tab
                enter
                """);
        using var replayUi = Demo();
        replayUi.RunScenarioText(recorded);
    }
}
