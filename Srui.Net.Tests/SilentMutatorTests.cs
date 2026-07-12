using Srui;
using Xunit;

namespace Srui.Net.Tests;

/// <summary>The protected silent mutators (ListBox.SetItemsSilently,
/// EditBox.SetTextSilently): a subclass input handler reshapes base-owned
/// state without triggering the public setters' focused re-announcement,
/// then owns the emission.</summary>
public class SilentMutatorTests
{
    /// <summary>A list where Space toggles a ", done" suffix on the
    /// selected item — the mutate-silently-then-emit pattern.</summary>
    private sealed class ToggleList : ListBox
    {
        private readonly List<string> _texts;
        private readonly HashSet<int> _done = [];

        public ToggleList(IWidgetContainer parent, IReadOnlyList<string> items)
            : base(parent, "Tasks", items, numbered: true)
        {
            _texts = new List<string>(items);
        }

        public void ShrinkTo(int count) => SetItemsSilently(_texts.GetRange(0, count));

        public void JumpTo(int index) => SelectSilently(index);

        public void Rename(string name) => SetNameSilently(name);

        protected override bool OnInput(in InputEvent input)
        {
            if (input.IsChar(' '))
            {
                var index = SelectedIndex;
                if (!_done.Add(index))
                    _done.Remove(index);
                SetItemsSilently(
                    _texts.Select((t, i) => _done.Contains(i) ? $"{t}, done" : t).ToList());
                Announce(_done.Contains(index) ? "Done." : "Not done.");
                PostChanged();
                return true;
            }
            return base.OnInput(input);
        }
    }

    /// <summary>An edit box where Up replaces the content with a canned
    /// recall, speaking only the recalled text.</summary>
    private sealed class RecallBox : EditBox
    {
        public RecallBox(IWidgetContainer parent) : base(parent, "Entry")
        {
        }

        protected override bool OnInput(in InputEvent input)
        {
            if (input.Kind == InputKind.MoveUp)
            {
                SetTextSilently("recalled entry");
                Announce("recalled entry");
                return true;
            }
            return base.OnInput(input);
        }
    }

    [Fact]
    public void SilentItemReplacementSpeaksOnlyTheSubclassEmission()
    {
        using var ui = new TestUi();
        var list = new ToggleList(ui.App, ["alpha", "beta"]);
        var changed = 0;
        list.Changed += () => changed++;
        list.Focus();
        ui.Drain();

        ui.Type(' ');
        Assert.Equal(new[] { "Done." }, ui.Spoken());
        Assert.Equal("alpha, done", list.SelectedItem?.Text);
        Assert.Equal(1, changed);

        ui.Type(' ');
        Assert.Equal(new[] { "Not done." }, ui.Spoken());
        Assert.Equal("alpha", list.SelectedItem?.Text);
    }

    [Fact]
    public void SilentItemReplacementKeepsFocusAnnouncementsCurrent()
    {
        using var ui = new TestUi();
        var list = new ToggleList(ui.App, ["alpha", "beta"]);
        list.Focus();
        ui.Drain();

        ui.Type(' ');
        ui.Drain();
        ui.Input(InputKind.SpeakFocus);
        var spoken = Assert.Single(ui.Spoken());
        Assert.Contains("alpha, done", spoken);
    }

    [Fact]
    public void SilentItemReplacementClampsSelectionSilently()
    {
        using var ui = new TestUi();
        var list = new ToggleList(ui.App, ["alpha", "beta", "gamma"]);
        list.Focus();
        ui.Input(InputKind.MoveToDocEnd);
        Assert.Equal(2, list.SelectedIndex);
        ui.Drain();

        list.ShrinkTo(1);
        Assert.Empty(ui.Spoken());
        Assert.Equal(0, list.SelectedIndex);
        Assert.Equal("alpha", list.SelectedItem?.Text);
    }

    [Fact]
    public void SilentSelectionMovesWithoutSpeakingAndClamps()
    {
        using var ui = new TestUi();
        var list = new ToggleList(ui.App, ["alpha", "beta", "gamma"]);
        list.Focus();
        ui.Drain();

        list.JumpTo(2);
        Assert.Empty(ui.Spoken());
        Assert.Equal("gamma", list.SelectedItem?.Text);

        list.JumpTo(99);
        Assert.Equal(2, list.SelectedIndex);

        ui.Input(InputKind.SpeakFocus);
        Assert.Contains(ui.Spoken(), s => s.Contains("gamma"));
    }

    [Fact]
    public void SilentRenameSaysNothingButReadsOnTheNextAnnouncement()
    {
        using var ui = new TestUi();
        var list = new ToggleList(ui.App, ["alpha"]);
        list.Focus();
        ui.Drain();

        list.Rename("Documents");
        Assert.Empty(ui.Spoken());

        ui.Input(InputKind.SpeakFocus);
        Assert.Contains(ui.Spoken(), s => s.Contains("Documents"));
    }

    [Fact]
    public void SilentTextReplacementSpeaksOnlyTheSubclassEmission()
    {
        using var ui = new TestUi();
        var box = new RecallBox(ui.App);
        box.Focus();
        ui.Drain();

        ui.Input(InputKind.MoveUp);
        Assert.Equal(new[] { "recalled entry" }, ui.Spoken());
        Assert.Equal("recalled entry", box.Text);
        Assert.Equal("recalled entry".Length, box.CursorPosition);
        Assert.Null(box.Selection);
    }

    [Fact]
    public void SubclassAnnouncesCarryTheirSource()
    {
        using var ui = new TestUi();
        var box = new RecallBox(ui.App);
        box.Focus();
        ui.Drain();

        ui.Input(InputKind.MoveUp);
        ui.App.DispatchEvents();
        var announce = Assert.IsType<AccessibilityEvent.Announce>(
            ui.Reader.Events.Single(e => e is AccessibilityEvent.Announce));
        Assert.Same(box, announce.Source);
    }
}
