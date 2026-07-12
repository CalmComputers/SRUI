using Srui;
using Xunit;

namespace Srui.Net.Tests;

/// <summary>Records what a reader hears, for assertion.</summary>
internal sealed class TestReader : IReader
{
    public readonly List<AccessibilityEvent> Events = new();
    public int Interrupts;

    public void OnEvent(AccessibilityEvent e) => Events.Add(e);

    public void OnInterrupt() => Interrupts++;
}

/// <summary>A headless app with a recording reader — the harness every
/// public-surface test drives: build widgets, push input, assert what
/// the reader hears.</summary>
internal sealed class TestUi : IDisposable
{
    public readonly SruiApp App = SruiApp.Headless();
    public readonly TestReader Reader = new();

    public TestUi() => App.AddReader(Reader);

    public void Dispose() => App.Dispose();

    /// <summary>Deliver queued output and return the utterances heard
    /// since the last call, in order.</summary>
    public List<string> Spoken()
    {
        App.DispatchEvents();
        var result = Reader.Events
            .Select(SpeechRenderer.RenderEvent)
            .Where(s => s is not null)
            .Select(s => s!)
            .ToList();
        Reader.Events.Clear();
        return result;
    }

    /// <summary>Deliver queued output, discarding it.</summary>
    public void Drain()
    {
        App.DispatchEvents();
        Reader.Events.Clear();
    }

    public bool Input(InputKind kind) => App.HandleInput(InputEvent.Simple(kind));

    public bool Input(InputEvent ev) => App.HandleInput(ev);

    public bool Type(char c) => App.HandleInput(InputEvent.TypeChar(c));

    public bool Raw(KeyCombo combo)
    {
        var (key, mods) = combo.ToFlat();
        return App.HandleInput(InputEvent.RawKey(key, mods));
    }
}

public class FocusAndNavigationTests
{
    private static (TestUi Ui, Button Save, Group Options, CheckBox Wrap) DemoUi()
    {
        var ui = new TestUi();
        var save = new Button(ui.App, "Save");
        var options = new Group(ui.App, "Options");
        var wrap = new CheckBox(options, "Word Wrap");
        return (ui, save, options, wrap);
    }

    [Fact]
    public void EnsureFocusAnnouncesFirstFocusable()
    {
        var (ui, save, _, _) = DemoUi();
        Assert.True(ui.App.EnsureFocus());
        Assert.True(save.IsFocused);
        Assert.Equal(new[] { "Save button" }, ui.Spoken());
    }

    [Fact]
    public void FirstTabEstablishesFocusWithoutMoving()
    {
        var (ui, save, _, _) = DemoUi();
        Assert.True(ui.Input(InputKind.NavigateNext));
        Assert.True(save.IsFocused);
    }

    [Fact]
    public void TabMovesAndAnnounces()
    {
        var (ui, _, _, wrap) = DemoUi();
        ui.App.EnsureFocus();
        ui.Drain();

        ui.Input(InputKind.NavigateNext);
        Assert.True(wrap.IsFocused);
        Assert.Equal(new[] { "Word Wrap check box not checked" }, ui.Spoken());
    }

    [Fact]
    public void CoalescingKeepsOnlySettledFocus()
    {
        var (ui, save, _, _) = DemoUi();
        ui.App.EnsureFocus();
        ui.Input(InputKind.NavigateNext);
        ui.Input(InputKind.NavigateNext); // wraps back to Save
        Assert.True(save.IsFocused);
        Assert.Equal(new[] { "Save button" }, ui.Spoken());
    }

    [Fact]
    public void FocusRecoversWhenFocusedWidgetRemoved()
    {
        var (ui, _, _, wrap) = DemoUi();
        wrap.Focus();
        ui.Drain();

        wrap.Remove();
        Assert.False(wrap.IsFocused);
        Assert.Single(ui.Spoken());
    }

    [Fact]
    public void FocusRecoversWhenFocusedSubtreeRemoved()
    {
        var (ui, save, options, wrap) = DemoUi();
        wrap.Focus();
        ui.Drain();

        options.Remove();
        Assert.True(save.IsFocused);
    }

    [Fact]
    public void FocusMemoryRestoresLastChildOnReentry()
    {
        var ui = new TestUi();
        var group = new Group(ui.App, "Options");
        _ = new CheckBox(group, "First");
        var second = new CheckBox(group, "Second");

        second.Focus();
        ui.Input(InputKind.TreeUp);
        Assert.True(group.IsFocused);
        ui.Input(InputKind.TreeDown);
        Assert.True(second.IsFocused);
    }

    [Fact]
    public void SpeakFocusReannounces()
    {
        var (ui, _, _, _) = DemoUi();
        ui.App.EnsureFocus();
        ui.Drain();

        ui.Input(InputKind.SpeakFocus);
        Assert.Equal(new[] { "Save button" }, ui.Spoken());
    }

    [Fact]
    public void HidingFocusedWidgetRecoversFocus()
    {
        var (ui, save, _, wrap) = DemoUi();
        wrap.Focus();
        ui.Drain();

        wrap.Hidden = true;
        Assert.True(save.IsFocused);
        Assert.Equal(new[] { "Save button" }, ui.Spoken());
    }

    [Fact]
    public void DisablingFocusedWidgetKeepsFocus()
    {
        var (ui, _, _, wrap) = DemoUi();
        wrap.Focus();
        ui.Drain();

        wrap.Disabled = true;
        Assert.True(wrap.IsFocused);
        Assert.Equal(new[] { "unavailable" }, ui.Spoken());

        wrap.Disabled = false;
        Assert.True(wrap.IsFocused);
        Assert.Equal(new[] { "available" }, ui.Spoken());
    }

    [Fact]
    public void TabReachesDisabledWidgets()
    {
        var (ui, save, _, wrap) = DemoUi();
        wrap.Disabled = true;
        save.Focus();
        ui.Drain();

        ui.Input(InputKind.NavigateNext);
        Assert.True(wrap.IsFocused);
        var spoken = Assert.Single(ui.Spoken());
        Assert.Contains("unavailable", spoken);
    }

    [Fact]
    public void DisabledFocusedWidgetIsInert()
    {
        var (ui, _, _, wrap) = DemoUi();
        var toggles = 0;
        wrap.Toggled += _ => toggles++;
        wrap.Disabled = true;
        wrap.Focus();
        ui.Drain();

        // Space would toggle an enabled checkbox; here it falls through
        // the whole claim order unconsumed.
        Assert.False(ui.Type(' '));
        ui.Drain();
        Assert.Equal(0, toggles);
        Assert.False(wrap.Checked);
    }

    [Fact]
    public void DisabledFocusedWidgetKeyBindingsAreInert()
    {
        var (ui, _, _, wrap) = DemoUi();
        var fired = 0;
        wrap.BindKey(KeyCombo.Plain(Key.Char('d')), KeyPhase.Press, () => fired++);
        wrap.Focus();
        ui.Drain();

        Assert.True(ui.App.HandleKey(new KeyInput(Keys.Char('d'), Mods.None, KeyPhase.Press)));
        Assert.Equal(1, fired);

        wrap.Disabled = true;
        ui.Drain();
        Assert.False(ui.App.HandleKey(new KeyInput(Keys.Char('d'), Mods.None, KeyPhase.Press)));
        Assert.Equal(1, fired);
    }

    [Fact]
    public void HidingGroupContainingFocusRecovers()
    {
        var (ui, save, options, wrap) = DemoUi();
        wrap.Focus();
        ui.Drain();

        options.Hidden = true;
        Assert.True(save.IsFocused);
        // The hidden subtree is no longer tab-reachable.
        ui.Drain();
        ui.Input(InputKind.NavigateNext);
        Assert.True(save.IsFocused);
    }

    [Fact]
    public void UnhideRestoresReachability()
    {
        var (ui, _, _, wrap) = DemoUi();
        wrap.Hidden = true;
        wrap.Hidden = false;
        wrap.Focus();
        Assert.True(wrap.IsFocused);
        Assert.False(wrap.Hidden);
    }

    [Fact]
    public void RenameSpeaksNewNameWhenFocused()
    {
        var (ui, save, _, _) = DemoUi();
        save.Focus();
        ui.Drain();

        save.Name = "Save All";
        Assert.Equal(new[] { "Save All" }, ui.Spoken());
        Assert.Equal("Save All", save.Name);
    }

    [Fact]
    public void DescriptionSpeaksDeltaWhenFocusedOnly()
    {
        var (ui, save, _, wrap) = DemoUi();
        save.Focus();
        ui.Drain();

        // Unfocused widget: silent.
        wrap.Description = "wraps long lines";
        Assert.Empty(ui.Spoken());

        // Focused widget: the new description alone.
        save.Description = "saves the file";
        Assert.Equal(new[] { "saves the file" }, ui.Spoken());

        // No-op mutation: silent.
        save.Description = "saves the file";
        Assert.Empty(ui.Spoken());
    }

    [Fact]
    public void LabelDeltasForDistinctPartsAllSurviveOneBatch()
    {
        var (ui, save, _, _) = DemoUi();
        save.Focus();
        ui.Drain();

        // Rename twice and describe once before draining: the settled
        // name and the description both speak; the intermediate name is
        // coalesced away.
        save.Name = "Save All";
        save.Name = "Save Everything";
        save.Description = "saves the file";
        Assert.Equal(new[] { "Save Everything", "saves the file" }, ui.Spoken());
    }

    [Fact]
    public void StateFlagsSpeakTransitionsWhenFocused()
    {
        var ui = new TestUi();
        var name = new EditBox(ui.App, "Name");
        name.Focus();
        ui.Drain();

        name.Required = true;
        Assert.Equal(new[] { "required" }, ui.Spoken());
        name.Warning = true;
        Assert.Equal(new[] { "warning" }, ui.Spoken());
        name.Required = false;
        Assert.Equal(new[] { "not required" }, ui.Spoken());
        name.Warning = false;
        Assert.Equal(new[] { "warning cleared" }, ui.Spoken());
    }

    [Fact]
    public void ShortcutChangesAreSilentWhenFocused()
    {
        var (ui, save, _, _) = DemoUi();
        save.Focus();
        ui.Drain();

        save.AddShortcut(KeyCombo.WithCtrl(Key.Char('s')));
        Assert.Empty(ui.Spoken());
    }

    [Fact]
    public void RequiredAndWarningAnnounce()
    {
        var ui = new TestUi();
        var name = new EditBox(ui.App, "Name");
        name.Required = true;
        name.Warning = true;
        name.Focus();
        Assert.Equal(new[] { "Name edit blank required warning" }, ui.Spoken());
        Assert.True(name.Required);
        Assert.True(name.Warning);
    }

    [Fact]
    public void UnhandledInputIsUnconsumed()
    {
        var (ui, _, _, _) = DemoUi();
        ui.App.EnsureFocus();
        // A raw key nothing claims falls through to the host.
        Assert.False(ui.Raw(KeyCombo.WithCtrl(Key.Char('s'))));
    }

    [Fact]
    public void UnhandledInputHookReceivesLeftovers()
    {
        var (ui, _, _, _) = DemoUi();
        ui.App.EnsureFocus();
        InputEvent? seen = null;
        ui.App.UnhandledInput = input =>
        {
            seen = input;
            return true;
        };
        Assert.True(ui.Raw(KeyCombo.WithCtrl(Key.Char('s'))));
        Assert.True(seen?.IsRawKey(Keys.Char('s'), Mods.Ctrl));
    }
}

public class ActivationTests
{
    [Fact]
    public void ButtonActivatesOnEnterAndSpace()
    {
        var ui = new TestUi();
        var save = new Button(ui.App, "Save");
        var presses = 0;
        save.Activated += () => presses++;
        save.Focus();
        ui.Drain();

        Assert.True(ui.Input(InputKind.Activate));
        Assert.True(ui.Type(' '));
        ui.Drain();
        Assert.Equal(2, presses);
    }

    [Fact]
    public void SecondaryActivateRaisesItsEvent()
    {
        var ui = new TestUi();
        var save = new Button(ui.App, "Save");
        var secondary = 0;
        save.SecondaryActivated += () => secondary++;
        save.Focus();
        ui.Drain();

        Assert.True(ui.Input(InputKind.SecondaryActivate));
        ui.Drain();
        Assert.Equal(1, secondary);
    }

    [Fact]
    public void ActivateItemsListClaimsEnterOverPrimary()
    {
        var ui = new TestUi();
        var files = new ListBox(
            ui.App, "Files", new[] { "Alpha", "Beta" }, activateItems: true);
        var ok = new Button(ui.App, "OK");
        var opens = 0;
        var presses = 0;
        files.Activated += () => opens++;
        ok.Activated += () => presses++;
        ui.App.SetPrimary(ok);
        files.Focus();
        ui.Drain();

        Assert.True(ui.Input(InputKind.Activate));
        ui.Drain();
        Assert.Equal(1, opens);
        Assert.Equal(0, presses);
    }

    [Fact]
    public void EmptyActivateItemsListLetsEnterFallThrough()
    {
        var ui = new TestUi();
        var files = new ListBox(
            ui.App, "Files", Array.Empty<string>(), activateItems: true);
        var ok = new Button(ui.App, "OK");
        var opens = 0;
        var presses = 0;
        files.Activated += () => opens++;
        ok.Activated += () => presses++;
        ui.App.SetPrimary(ok);
        files.Focus();
        ui.Drain();

        Assert.True(ui.Input(InputKind.Activate));
        ui.Drain();
        Assert.Equal(0, opens);
        Assert.Equal(1, presses);
    }

    [Fact]
    public void EnterOnCheckboxFallsThroughToPrimary()
    {
        var ui = new TestUi();
        var wrap = new CheckBox(ui.App, "Word Wrap");
        var ok = new Button(ui.App, "OK");
        var presses = 0;
        ok.Activated += () => presses++;
        ui.App.SetPrimary(ok);
        wrap.Focus();
        ui.Drain();

        Assert.True(ui.Input(InputKind.Activate));
        ui.Drain();
        Assert.Equal(1, presses);
        Assert.False(wrap.Checked);
    }

    [Fact]
    public void HiddenPrimaryDoesNotActivate()
    {
        var ui = new TestUi();
        var wrap = new CheckBox(ui.App, "Word Wrap");
        var ok = new Button(ui.App, "OK");
        var presses = 0;
        ok.Activated += () => presses++;
        ui.App.SetPrimary(ok);
        ok.Hidden = true;
        wrap.Focus();
        ui.Drain();

        Assert.False(ui.Input(InputKind.Activate));
        ui.Drain();
        Assert.Equal(0, presses);
    }

    [Fact]
    public void DisabledPrimaryDoesNotActivate()
    {
        var ui = new TestUi();
        var wrap = new CheckBox(ui.App, "Word Wrap");
        var ok = new Button(ui.App, "OK");
        var presses = 0;
        ok.Activated += () => presses++;
        ui.App.SetPrimary(ok);
        ok.Disabled = true;
        wrap.Focus();
        ui.Drain();

        Assert.False(ui.Input(InputKind.Activate));
        ui.Drain();
        Assert.Equal(0, presses);
    }

    [Fact]
    public void PrimaryUnderHiddenAncestorDoesNotActivate()
    {
        var ui = new TestUi();
        var wrap = new CheckBox(ui.App, "Word Wrap");
        var panel = new Group(ui.App, "Panel");
        var ok = new Button(panel, "OK");
        var presses = 0;
        ok.Activated += () => presses++;
        ui.App.SetPrimary(ok);
        panel.Hidden = true;
        wrap.Focus();
        ui.Drain();

        Assert.False(ui.Input(InputKind.Activate));
        ui.Drain();
        Assert.Equal(0, presses);
    }

    [Fact]
    public void DismissActivatesCancel()
    {
        var ui = new TestUi();
        _ = new Button(ui.App, "Body");
        var cancel = new Button(ui.App, "Cancel");
        var presses = 0;
        cancel.Activated += () => presses++;
        ui.App.SetCancel(cancel);
        ui.App.EnsureFocus();
        ui.Drain();

        Assert.True(ui.Input(InputKind.Dismiss));
        ui.Drain();
        Assert.Equal(1, presses);
    }

    [Fact]
    public void DisabledCancelLeavesDismissUnconsumed()
    {
        var ui = new TestUi();
        _ = new Button(ui.App, "Body");
        var cancel = new Button(ui.App, "Cancel");
        var presses = 0;
        cancel.Activated += () => presses++;
        ui.App.SetCancel(cancel);
        cancel.Disabled = true;
        ui.App.EnsureFocus();
        ui.Drain();

        Assert.False(ui.Input(InputKind.Dismiss));
        ui.Drain();
        Assert.Equal(0, presses);
    }

    [Fact]
    public void DismissUnconsumedWithoutCancel()
    {
        var ui = new TestUi();
        _ = new Button(ui.App, "Save");
        ui.App.EnsureFocus();
        Assert.False(ui.Input(InputKind.Dismiss));
    }

    [Fact]
    public void AnyWidgetCanBeActivated()
    {
        // Activation is a Widget-level concept: an Activate shortcut on a
        // slider raises its Activated event like a button press would.
        var ui = new TestUi();
        var volume = new Slider(ui.App, "Volume", 50, 0, 100);
        var other = new Button(ui.App, "Other");
        var fired = 0;
        volume.Activated += () => fired++;
        volume.AddShortcut(KeyCombo.WithCtrl(Key.Char('m')), ShortcutAction.Activate);
        other.Focus();
        ui.Drain();

        Assert.True(ui.Raw(KeyCombo.WithCtrl(Key.Char('m'))));
        ui.Drain();
        Assert.Equal(1, fired);
        Assert.True(other.IsFocused);
    }
}

public class ShortcutTests
{
    private static (TestUi Ui, Button Save, Group Options, CheckBox Wrap) DemoUi()
    {
        var ui = new TestUi();
        var save = new Button(ui.App, "Save");
        var options = new Group(ui.App, "Options");
        var wrap = new CheckBox(options, "Word Wrap");
        return (ui, save, options, wrap);
    }

    [Fact]
    public void MnemonicJumpsToWidget()
    {
        var (ui, _, _, wrap) = DemoUi();
        wrap.AddShortcut(KeyCombo.WithAlt(Key.Char('w')));
        ui.App.EnsureFocus();
        ui.Drain();

        Assert.True(ui.Input(new InputEvent(InputKind.Shortcut, 'w', 0, Mods.None)));
        Assert.True(wrap.IsFocused);
    }

    [Fact]
    public void ShortcutJumpFocusesAndAnnounces()
    {
        var (ui, save, _, wrap) = DemoUi();
        wrap.AddShortcut(KeyCombo.WithCtrl(Key.Char('w')));
        save.Focus();
        ui.Drain();

        Assert.True(ui.Raw(KeyCombo.WithCtrl(Key.Char('w'))));
        Assert.True(wrap.IsFocused);
        Assert.Equal(
            new[] { "Word Wrap check box not checked control w" },
            ui.Spoken());
    }

    [Fact]
    public void ShortcutActivateFiresWithoutMovingFocus()
    {
        var (ui, save, _, wrap) = DemoUi();
        var presses = 0;
        save.Activated += () => presses++;
        save.AddShortcut(KeyCombo.WithCtrl(Key.Char('g')), ShortcutAction.Activate);
        wrap.Focus();
        ui.Drain();

        Assert.True(ui.Raw(KeyCombo.WithCtrl(Key.Char('g'))));
        var spoken = ui.Spoken();
        Assert.Equal(1, presses);
        Assert.True(wrap.IsFocused);
        Assert.Empty(spoken);
    }

    [Fact]
    public void ShortcutJumpAndActivateDoesBoth()
    {
        var (ui, save, _, wrap) = DemoUi();
        var presses = 0;
        save.Activated += () => presses++;
        save.AddShortcut(KeyCombo.WithCtrl(Key.Char('s')), ShortcutAction.JumpAndActivate);
        wrap.Focus();
        ui.Drain();

        Assert.True(ui.Raw(KeyCombo.WithCtrl(Key.Char('s'))));
        var spoken = ui.Spoken();
        Assert.True(save.IsFocused);
        Assert.Equal(1, presses);
        Assert.Equal(new[] { "Save button control s" }, spoken);
    }

    [Fact]
    public void ShortcutOnUnreachableWidgetIsInert()
    {
        var (ui, save, options, wrap) = DemoUi();
        wrap.AddShortcut(KeyCombo.WithCtrl(Key.Char('w')));
        save.Focus();
        ui.Drain();

        var ctrlW = KeyCombo.WithCtrl(Key.Char('w'));

        wrap.Disabled = true;
        Assert.False(ui.Raw(ctrlW));
        Assert.True(save.IsFocused);

        wrap.Disabled = false;
        options.Hidden = true;
        Assert.False(ui.Raw(ctrlW));
        Assert.True(save.IsFocused);
    }

    [Fact]
    public void FocusedWidgetBeatsShortcut()
    {
        var ui = new TestUi();
        var notes = new EditBox(ui.App, "Notes");
        var other = new Button(ui.App, "Other");
        other.AddShortcut(KeyCombo.Plain(Key.Char('x')));
        other.Focus();
        ui.Drain();
        // Jumping to the already-focused widget is a consumed no-op.
        Assert.True(ui.Type('x'));
        Assert.True(other.IsFocused);

        // ...but an edit box consumes the keystroke first.
        notes.Focus();
        ui.Drain();
        Assert.True(ui.Type('x'));
        Assert.True(notes.IsFocused);
        Assert.Equal("x", notes.Text);
    }

    [Fact]
    public void FirstClaimantInTreeOrderWins()
    {
        var ui = new TestUi();
        var first = new Button(ui.App, "First");
        var second = new Button(ui.App, "Second");
        var combo = KeyCombo.WithCtrl(Key.Char('k'));
        second.AddShortcut(combo);
        first.AddShortcut(combo);
        second.Focus();
        ui.Drain();

        Assert.True(ui.Raw(combo));
        Assert.True(first.IsFocused);

        // When the first claimant becomes unreachable, the next one wins.
        first.Hidden = true;
        ui.Drain();
        second.Focus();
        Assert.True(ui.Raw(combo));
        Assert.True(second.IsFocused);
    }

    [Fact]
    public void ShortcutsInLowerLayersAreInert()
    {
        var ui = new TestUi();
        var save = new Button(ui.App, "Save");
        var presses = 0;
        save.Activated += () => presses++;
        var combo = KeyCombo.WithCtrl(Key.Char('s'));
        save.AddShortcut(combo, ShortcutAction.Activate);
        ui.App.EnsureFocus();
        ui.Drain();

        var dialog = ui.App.OpenDialog();
        _ = new Button(dialog, "Confirm");
        ui.App.EnsureFocus();
        ui.Drain();
        Assert.False(ui.Raw(combo));
        ui.Drain();
        Assert.Equal(0, presses);

        dialog.Close();
        ui.Drain();
        Assert.True(ui.Raw(combo));
        ui.Drain();
        Assert.Equal(1, presses);
    }

    [Fact]
    public void ClearShortcutsRemovesBindingsAndAnnouncement()
    {
        var (ui, save, _, _) = DemoUi();
        save.AddShortcut(KeyCombo.WithCtrl(Key.Char('s')), ShortcutAction.Activate);
        save.ClearShortcuts();
        ui.App.EnsureFocus();

        Assert.Equal(new[] { "Save button" }, ui.Spoken());
        Assert.False(ui.Raw(KeyCombo.WithCtrl(Key.Char('s'))));
    }

    [Fact]
    public void ShortcutMatchingUsesThePhysicalCombo()
    {
        var ui = new TestUi();
        var arena = new CustomWidget(ui.App, "Arena");
        var wordDelete = new Button(ui.App, "Word delete");
        var fired = 0;
        wordDelete.Activated += () => fired++;
        wordDelete.AddShortcut(KeyCombo.WithShift(Key.Backspace), ShortcutAction.Activate);
        arena.Focus();
        ui.Drain();

        // ctrl+backspace and plain backspace are different combos even
        // where kinds overlap: no match.
        Assert.False(ui.Input(
            new InputEvent(InputKind.DeleteWordBackward, 0, Keys.Backspace, Mods.Ctrl)));
        Assert.False(ui.Input(
            new InputEvent(InputKind.DeleteBackward, 0, Keys.Backspace, Mods.None)));
        ui.Drain();
        Assert.Equal(0, fired);

        Assert.True(ui.Input(
            new InputEvent(InputKind.DeleteBackward, 0, Keys.Backspace, Mods.Shift)));
        ui.Drain();
        Assert.Equal(1, fired);
    }

    [Fact]
    public void PlainLetterShortcutIgnoresShiftedTyping()
    {
        var ui = new TestUi();
        var arena = new CustomWidget(ui.App, "Arena");
        var other = new Button(ui.App, "Other");
        other.AddShortcut(KeyCombo.Plain(Key.Char('x')));
        arena.Focus();
        ui.Drain();

        // 'X' with shift carries (x, shift): not the plain-x shortcut.
        Assert.False(ui.Input(
            new InputEvent(InputKind.TypeChar, 'X', Keys.Char('x'), Mods.Shift)));
        Assert.True(arena.IsFocused);

        // Unshifted x still jumps.
        Assert.True(ui.Input(
            new InputEvent(InputKind.TypeChar, 'x', Keys.Char('x'), Mods.None)));
        Assert.True(other.IsFocused);
    }

}

public class KeyBindingTests
{
    private static KeyInput Transition(KeyCombo combo, KeyPhase phase)
    {
        var (key, mods) = combo.ToFlat();
        return new KeyInput(key, mods, phase);
    }

    [Fact]
    public void BindKeyFiresOnlyWhileFocused()
    {
        var ui = new TestUi();
        var arena = new CustomWidget(ui.App, "Arena");
        var other = new Button(ui.App, "Other");
        var presses = 0;
        var q = KeyCombo.Plain(Key.Char('q'));
        arena.BindKey(q, KeyPhase.Press, () => presses++);

        arena.Focus();
        ui.Drain();
        Assert.True(ui.App.HandleKey(Transition(q, KeyPhase.Press)));
        Assert.Equal(1, presses);

        other.Focus();
        ui.Drain();
        Assert.False(ui.App.HandleKey(Transition(q, KeyPhase.Press)));
        Assert.Equal(1, presses);
    }

    [Fact]
    public void PressMatchesExactComboReleaseMatchesKeyAlone()
    {
        var ui = new TestUi();
        var arena = new CustomWidget(ui.App, "Arena");
        var q = KeyCombo.Plain(Key.Char('q'));
        var pressed = 0;
        var released = 0;
        arena.BindKey(q, KeyPhase.Press, () => pressed++);
        arena.BindKey(q, KeyPhase.Release, () => released++);
        arena.Focus();
        ui.Drain();

        // Shift+Q is a different press binding, but the same release.
        Assert.False(ui.App.HandleKey(
            Transition(KeyCombo.WithShift(Key.Char('q')), KeyPhase.Press)));
        Assert.Equal(0, pressed);
        Assert.True(ui.App.HandleKey(
            Transition(KeyCombo.WithShift(Key.Char('q')), KeyPhase.Release)));
        Assert.Equal(1, released);
    }

    [Fact]
    public void ReleaseBindingWithModifiersThrows()
    {
        var ui = new TestUi();
        var arena = new CustomWidget(ui.App, "Arena");
        Assert.Throws<ArgumentException>(() => arena.BindKey(
            KeyCombo.WithShift(Key.Char('q')), KeyPhase.Release, () => { }));
    }

    [Fact]
    public void UnbindKeyRemovesEveryHandlerForTheCombo()
    {
        var ui = new TestUi();
        var arena = new CustomWidget(ui.App, "Arena");
        var presses = 0;
        var q = KeyCombo.Plain(Key.Char('q'));
        arena.BindKey(q, KeyPhase.Press, () => presses++);
        arena.BindKey(q, KeyPhase.Press, () => presses++);
        arena.Focus();
        ui.Drain();

        Assert.True(arena.UnbindKey(q, KeyPhase.Press));
        Assert.False(arena.UnbindKey(q, KeyPhase.Press));
        Assert.False(ui.App.HandleKey(Transition(q, KeyPhase.Press)));
        Assert.Equal(0, presses);
    }
}

public class DialogTests
{
    [Fact]
    public void LayerPopRestoresAndAnnouncesFocus()
    {
        var ui = new TestUi();
        var save = new Button(ui.App, "Save");
        save.Focus();
        ui.Drain();

        var dialog = ui.App.OpenDialog();
        var confirm = new Button(dialog, "Confirm");
        confirm.Focus();
        ui.Drain();

        dialog.Close();
        Assert.True(save.IsFocused);
        Assert.Equal(new[] { "Save button" }, ui.Spoken());
    }

    [Fact]
    public void AnnounceOpenedCollectsPrecedingLabels()
    {
        var ui = new TestUi();
        _ = new Button(ui.App, "Body");
        ui.App.EnsureFocus();
        ui.Drain();

        var dialog = ui.App.OpenDialog();
        _ = new Label(dialog, "Delete 3 files?");
        _ = new Button(dialog, "Yes");
        dialog.AnnounceOpened();
        Assert.Equal(new[] { "Delete 3 files? Yes button" }, ui.Spoken());
    }

    [Fact]
    public void EscapeDismissesDialogWithoutCancelWidget()
    {
        var ui = new TestUi();
        var save = new Button(ui.App, "Save");
        save.Focus();
        ui.Drain();

        var dialog = ui.App.OpenDialog();
        _ = new Button(dialog, "Confirm");
        dialog.AnnounceOpened();
        ui.Drain();

        var dismissed = 0;
        var closed = 0;
        dialog.Dismissed += () => dismissed++;
        dialog.Closed += () => closed++;

        Assert.True(ui.Input(InputKind.Dismiss));
        Assert.Equal(1, dismissed);
        Assert.Equal(1, closed);
        Assert.False(dialog.IsOpen);
        Assert.True(save.IsFocused);
    }

    [Fact]
    public void ClosingBuriedDialogClosesThoseAboveIt()
    {
        var ui = new TestUi();
        _ = new Button(ui.App, "Root");
        ui.App.EnsureFocus();
        ui.Drain();

        var outer = ui.App.OpenDialog();
        _ = new Button(outer, "Outer");
        var inner = ui.App.OpenDialog();
        _ = new Button(inner, "Inner");

        var innerClosed = 0;
        inner.Closed += () => innerClosed++;

        outer.Close();
        Assert.False(outer.IsOpen);
        Assert.False(inner.IsOpen);
        Assert.Equal(1, innerClosed);
    }

    [Fact]
    public void DialogResultIsSpokenBeforeRestoredFocus()
    {
        var ui = new TestUi();
        var save = new Button(ui.App, "Save");
        save.Focus();
        ui.Drain();

        // The natural handler shape: close, then report the result. The
        // restored lower-layer focus must be heard AFTER the result.
        var dialog = ui.App.OpenDialog();
        var create = new Button(dialog, "Create");
        create.Activated += () =>
        {
            dialog.Close();
            ui.App.Announce("Created playlist Untitled.");
        };
        ui.App.SetPrimary(create);
        dialog.AnnounceOpened();
        ui.Drain();

        Assert.True(ui.Input(InputKind.Activate));
        Assert.Equal(new[] { "Created playlist Untitled.", "Save button" }, ui.Spoken());
    }

    [Fact]
    public void ConfirmDialogRoutesChoices()
    {
        var ui = new TestUi();
        _ = new Button(ui.App, "Root");
        ui.App.EnsureFocus();
        ui.Drain();

        string? outcome = null;
        ui.App.Confirm("Overwrite?", onYes: () => outcome = "yes", onNo: () => outcome = "no");
        var spoken = ui.Spoken();
        Assert.Equal("Overwrite? Yes button alt y", spoken[^1]);

        // Enter presses Yes (the dialog's primary).
        Assert.True(ui.Input(InputKind.Activate));
        ui.Drain();
        Assert.Equal("yes", outcome);

        // Escape counts as no.
        outcome = null;
        ui.App.Confirm("Again?", onYes: () => outcome = "yes", onNo: () => outcome = "no");
        ui.Drain();
        Assert.True(ui.Input(InputKind.Dismiss));
        ui.Drain();
        Assert.Equal("no", outcome);
    }
}

public class ListBoxTests
{
    private static (TestUi Ui, ListBox Files) ListUi(bool numbered)
    {
        var ui = new TestUi();
        var files = new ListBox(
            ui.App, "Files", ["alpha.txt", "bravo.txt", "charlie.txt"], numbered);
        files.Focus();
        ui.Drain();
        return (ui, files);
    }

    [Fact]
    public void FocusAnnouncementIncludesValueAndPosition()
    {
        var ui = new TestUi();
        var files = new ListBox(
            ui.App, "Files", ["alpha.txt", "bravo.txt", "charlie.txt"], numbered: true);
        files.Focus();
        Assert.Equal(new[] { "Files list alpha.txt 1 of 3" }, ui.Spoken());
    }

    [Fact]
    public void ArrowsAnnounceItems()
    {
        var (ui, files) = ListUi(true);
        var changes = 0;
        files.Changed += () => changes++;

        Assert.True(ui.Input(InputKind.MoveDown));
        Assert.Equal(new[] { "bravo.txt 2 of 3" }, ui.Spoken());
        Assert.Equal(1, changes);
        Assert.Equal(1, files.SelectedIndex);
        Assert.Equal("bravo.txt", files.SelectedItem?.Text);
    }

    [Fact]
    public void UnnumberedAnnouncesBareItem()
    {
        var (ui, _) = ListUi(false);
        ui.Input(InputKind.MoveDown);
        Assert.Equal(new[] { "bravo.txt" }, ui.Spoken());
    }

    [Fact]
    public void BoundariesAnnounceWithoutMoving()
    {
        var (ui, files) = ListUi(true);

        ui.Input(InputKind.MoveUp);
        Assert.Equal(new[] { "top, alpha.txt 1 of 3" }, ui.Spoken());
        Assert.Equal(0, files.SelectedIndex);

        ui.Input(InputKind.MoveToDocEnd);
        ui.Drain();
        ui.Input(InputKind.MoveDown);
        Assert.Equal(new[] { "bottom, charlie.txt 3 of 3" }, ui.Spoken());
    }

    [Fact]
    public void HomeEndJump()
    {
        var (ui, files) = ListUi(true);

        ui.Input(InputKind.MoveToDocEnd);
        Assert.Equal(2, files.SelectedIndex);
        Assert.Equal(new[] { "charlie.txt 3 of 3" }, ui.Spoken());

        ui.Input(InputKind.MoveToDocStart);
        Assert.Equal(0, files.SelectedIndex);
    }

    [Fact]
    public void EnterInListActivatesPrimary()
    {
        var (ui, files) = ListUi(true);
        var open = new Button(ui.App, "Open");
        string? chosen = null;
        open.Activated += () => chosen = files.SelectedItem?.Text;
        ui.App.SetPrimary(open);
        ui.Input(InputKind.MoveDown);
        ui.Drain();

        Assert.True(ui.Input(InputKind.Activate));
        ui.Drain();
        Assert.Equal("bravo.txt", chosen);
    }

    [Fact]
    public void EnterInListUnconsumedWithoutPrimary()
    {
        var (ui, _) = ListUi(true);
        Assert.False(ui.Input(InputKind.Activate));
        Assert.Empty(ui.Spoken());
    }

    [Fact]
    public void TypeaheadFirstLetterCycles()
    {
        var ui = new TestUi();
        var files = new ListBox(ui.App, "Files", ["apple", "banana", "avocado"]);
        files.Focus();
        ui.Drain();

        // 'a' from apple → next item starting with 'a' (wraps past banana).
        ui.App.SetNow(1000);
        ui.Type('a');
        Assert.Equal("avocado", files.SelectedItem?.Text);
        Assert.Equal(new[] { "avocado" }, ui.Spoken());

        // Repeated 'a' cycles onward: avocado → apple.
        ui.App.SetNow(1100);
        ui.Type('a');
        Assert.Equal("apple", files.SelectedItem?.Text);
    }

    [Fact]
    public void TypeaheadPrefixSearch()
    {
        var ui = new TestUi();
        var files = new ListBox(ui.App, "Files", ["banana", "berry", "cherry"]);
        files.Focus();
        ui.Drain();

        ui.App.SetNow(1000);
        ui.Type('b');
        // From banana, 'b' cycles to the NEXT b-item: berry.
        Assert.Equal("berry", files.SelectedItem?.Text);
        ui.App.SetNow(1100);
        ui.Type('e');
        // Buffer "be" → prefix search keeps berry.
        Assert.Equal("berry", files.SelectedItem?.Text);
    }

    [Fact]
    public void TypeaheadTimesOut()
    {
        var ui = new TestUi();
        var files = new ListBox(ui.App, "Files", ["banana", "berry", "cat"]);
        files.Focus();
        ui.Drain();

        ui.App.SetNow(1000);
        ui.Type('b');
        Assert.Equal("berry", files.SelectedItem?.Text);

        // 500ms later the buffer has expired: 'c' is a fresh first letter.
        ui.App.SetNow(1500);
        ui.Type('c');
        Assert.Equal("cat", files.SelectedItem?.Text);
    }

    [Fact]
    public void EmptyListAnswersArrowsAndConsumesNothingElse()
    {
        var ui = new TestUi();
        var files = new ListBox(ui.App, "Files", Array.Empty<string>(), numbered: true);
        files.Focus();
        Assert.Equal(new[] { "Files list empty" }, ui.Spoken());
        Assert.Equal(-1, files.SelectedIndex);
        Assert.Null(files.SelectedItem);

        // Arrows answer with what the label already says.
        Assert.True(ui.Input(InputKind.MoveDown));
        Assert.Equal(new[] { "empty" }, ui.Spoken());

        Assert.False(ui.Input(InputKind.Activate));
        Assert.Empty(ui.Spoken());
    }

    [Fact]
    public void SetItemsClampsAndSpeaksNewItemWhenFocused()
    {
        var (ui, files) = ListUi(true);
        ui.Input(InputKind.MoveToDocEnd);
        ui.Drain();

        files.SetItems(["only.txt"]);
        Assert.Equal(0, files.SelectedIndex);
        Assert.Equal(new[] { "only.txt" }, ui.Spoken());
        Assert.Equal(new[] { "only.txt" }, files.Items.Select(i => i.Text));
    }

    [Fact]
    public void SetItemsSilentWhenUnfocused()
    {
        var ui = new TestUi();
        var files = new ListBox(ui.App, "Files", ["a"]);
        var other = new Button(ui.App, "Other");
        other.Focus();
        ui.Drain();

        files.SetItems(["b"]);
        Assert.Empty(ui.Spoken());
    }

    [Fact]
    public void SelectedIndexSpeaksItemOnlyWhenFocused()
    {
        var (ui, files) = ListUi(true);

        files.SelectedIndex = 1;
        Assert.Equal(new[] { "bravo.txt 2 of 3" }, ui.Spoken());
        Assert.Equal(1, files.SelectedIndex);

        // Same index again: silent.
        files.SelectedIndex = 1;
        Assert.Empty(ui.Spoken());

        // Out-of-range clamps.
        files.SelectedIndex = 99;
        Assert.Equal(2, files.SelectedIndex);
        ui.Drain();

        // Unfocused: silent.
        var other = new Button(ui.App, "Other");
        other.Focus();
        ui.Drain();
        files.SelectedIndex = 0;
        Assert.Empty(ui.Spoken());
        Assert.Equal(0, files.SelectedIndex);
    }
}

public class EditBoxTests
{
    [Fact]
    public void FocusAnnouncement()
    {
        var ui = new TestUi();
        var notes = new EditBox(ui.App, "Notes");
        notes.Focus();
        Assert.Equal(new[] { "Notes edit blank" }, ui.Spoken());
    }

    [Fact]
    public void TypingEchoesAndUpdatesState()
    {
        var ui = new TestUi();
        var notes = new EditBox(ui.App, "Notes");
        var changes = 0;
        notes.Changed += () => changes++;
        notes.Focus();
        ui.Drain();

        Assert.True(ui.Type('h'));
        Assert.Equal(new[] { "h" }, ui.Spoken());
        Assert.Equal(1, changes);

        ui.Type('i');
        ui.Drain();
        Assert.Equal("hi", notes.Text);
    }

    [Fact]
    public void WordEchoOnBoundary()
    {
        var ui = new TestUi();
        var notes = new EditBox(ui.App, "Notes");
        notes.Focus();
        foreach (var ch in "hey")
            ui.Type(ch);
        ui.Drain();
        ui.Type(' ');
        Assert.Equal(new[] { "hey space" }, ui.Spoken());
    }

    [Fact]
    public void ArrowNavigationSpeaksChars()
    {
        var ui = new TestUi();
        var notes = new EditBox(ui.App, "Notes", "ab");
        notes.Focus();
        ui.Drain();

        // Cursor at 0; left is the top boundary.
        Assert.True(ui.Input(InputKind.MoveLeft));
        Assert.Equal(new[] { "Top, a" }, ui.Spoken());

        Assert.True(ui.Input(InputKind.MoveRight));
        Assert.Equal(new[] { "b" }, ui.Spoken());
    }

    [Fact]
    public void EnterSingleLineFallsThroughToPrimary()
    {
        var ui = new TestUi();
        var notes = new EditBox(ui.App, "Notes");
        var ok = new Button(ui.App, "OK");
        var presses = 0;
        ok.Activated += () => presses++;
        ui.App.SetPrimary(ok);
        notes.Focus();
        ui.Drain();

        Assert.True(ui.Input(InputKind.Activate));
        ui.Drain();
        Assert.Equal(1, presses);
        Assert.Equal("", notes.Text);
    }

    [Fact]
    public void EnterMultilineInsertsNewline()
    {
        var ui = new TestUi();
        var notes = new EditBox(ui.App, "Notes", multiline: true);
        notes.Focus();
        ui.Drain();

        Assert.True(ui.Input(InputKind.Activate));
        Assert.Equal(new[] { "new line" }, ui.Spoken());
        Assert.Equal("\n", notes.Text);
    }

    private sealed class MemClipboard : IClipboard
    {
        private string? _content;
        public string? Read() => _content;
        public void Write(string text) => _content = text;
    }

    [Fact]
    public void SelectAllAndCopyThroughInjectedClipboard()
    {
        var ui = new TestUi();
        ui.App.SetClipboard(new MemClipboard());
        var notes = new EditBox(ui.App, "Notes", "hello");
        notes.Focus();
        ui.Drain();

        ui.Input(InputKind.SelectAll);
        Assert.Equal(new[] { "hello selected" }, ui.Spoken());

        ui.Input(InputKind.Copy);
        Assert.Equal(new[] { "Copy" }, ui.Spoken());

        // Paste at the end doubles the text via the injected clipboard.
        ui.Input(InputKind.MoveToDocEnd);
        ui.Input(InputKind.Paste);
        ui.Drain();
        Assert.Equal("hellohello", notes.Text);
    }

    [Fact]
    public void SetTextSpeaksNewValueWhenFocused()
    {
        var ui = new TestUi();
        var notes = new EditBox(ui.App, "Notes", "old");
        notes.Focus();
        ui.Drain();

        notes.Text = "new text";
        Assert.Equal(new[] { "new text" }, ui.Spoken());
        Assert.Equal("new text", notes.Text);
    }

    [Fact]
    public void ReadOnlyToggleSpeaksNewRoleTextWhenFocused()
    {
        var ui = new TestUi();
        var notes = new EditBox(ui.App, "Notes", "body");
        notes.Focus();
        ui.Drain();

        notes.ReadOnly = true;
        Assert.Equal(new[] { "edit read only" }, ui.Spoken());
        notes.ReadOnly = false;
        Assert.Equal(new[] { "edit" }, ui.Spoken());
    }

    [Fact]
    public void ReadOnlyChangesRoleTextAndSwallowsTyping()
    {
        var ui = new TestUi();
        var notes = new EditBox(ui.App, "Notes", "body", multiline: true) { ReadOnly = true };
        notes.Focus();
        Assert.Equal(new[] { "Notes edit read only multi line body" }, ui.Spoken());

        Assert.True(ui.Type('x'));
        Assert.Empty(ui.Spoken());
        Assert.Equal("body", notes.Text);
    }

    [Fact]
    public void CursorPositionSpeaksLikeUserNavigation()
    {
        var ui = new TestUi();
        var notes = new EditBox(ui.App, "Notes", "abc");
        notes.Focus();
        ui.Drain();

        notes.CursorPosition = 1;
        Assert.Equal(1, notes.CursorPosition);
        Assert.Equal(new[] { "b" }, ui.Spoken());

        // Same position: silent. Out of range: clamps.
        notes.CursorPosition = 1;
        Assert.Empty(ui.Spoken());
        notes.CursorPosition = 99;
        Assert.Equal(3, notes.CursorPosition);
        ui.Drain();
    }

    [Fact]
    public void CursorPositionSnapsOffSurrogateHalves()
    {
        var ui = new TestUi();
        var notes = new EditBox(ui.App, "Notes", "a𐐷b"); // 𐐷 is astral: two UTF-16 units
        notes.CursorPosition = 2; // middle of the surrogate pair
        Assert.Equal(1, notes.CursorPosition);
    }

    [Fact]
    public void SelectionSurfaceSpeaksAndReads()
    {
        var ui = new TestUi();
        var notes = new EditBox(ui.App, "Notes", "abcd");
        notes.Focus();
        ui.Drain();

        notes.Selection = (1, 3);
        Assert.Equal((1, 3), notes.Selection);
        Assert.Equal("bc", notes.SelectedText);
        Assert.Equal(new[] { "bc selected" }, ui.Spoken());

        notes.Selection = null;
        Assert.Null(notes.Selection);
        Assert.Equal(new[] { "Selection removed" }, ui.Spoken());

        notes.SelectAll();
        Assert.Equal("abcd", notes.SelectedText);
        Assert.Equal(new[] { "abcd selected" }, ui.Spoken());
        Assert.Equal(4, notes.Length);
    }

    [Fact]
    public void WordQueriesMirrorTheCtrlArrowFamily()
    {
        var ui = new TestUi();
        var notes = new EditBox(ui.App, "Notes", "hello world");

        Assert.Equal(6, notes.NextWordStart(0));
        Assert.Equal(0, notes.PreviousWordStart(6));
        Assert.Equal("world", notes.WordAt(6));
        Assert.Equal("hello", notes.WordAt(3));
        Assert.Equal(6, notes.NextWordExtent(0));
        Assert.Equal(6, notes.PreviousWordExtent(11));
    }

    [Fact]
    public void WordStartsAndExtentsDivergeOnPunctuation()
    {
        var ui = new TestUi();
        var notes = new EditBox(ui.App, "Notes", "foo.. bar");

        // Ctrl+arrow stops on the punctuation run; the extent (what
        // Ctrl+Backspace/Delete act on) swallows it with the word.
        Assert.Equal(3, notes.NextWordStart(0));
        Assert.Equal(6, notes.NextWordExtent(0));
        Assert.Equal(3, notes.PreviousWordStart(6));
        Assert.Equal(0, notes.PreviousWordExtent(6));
    }

    [Fact]
    public void LineQueriesHandleCrlf()
    {
        var ui = new TestUi();
        var notes = new EditBox(ui.App, "Notes", "one\r\ntwo\nthree", multiline: true);

        Assert.Equal(5, notes.LineStartAt(6));
        Assert.Equal(8, notes.LineEndAt(6));
        Assert.Equal("two", notes.LineTextAt(6));
        Assert.Equal(3, notes.LineEndAt(0)); // before the CR
        Assert.Equal("three", notes.LineTextAt(notes.Length));
        Assert.Equal(9, notes.LineStartAt(99)); // clamps to the text end
    }

    [Fact]
    public void LineColumnConversionRoundTripsAndClamps()
    {
        var ui = new TestUi();
        var notes = new EditBox(ui.App, "Notes", "one\r\ntwo\nthree", multiline: true);

        Assert.Equal(3, notes.LineCount);
        Assert.Equal((0, 0), notes.LineColumnAt(0));
        Assert.Equal((1, 1), notes.LineColumnAt(6));
        Assert.Equal((2, 5), notes.LineColumnAt(notes.Length));

        Assert.Equal(6, notes.PositionAt(1, 1));
        Assert.Equal(3, notes.PositionAt(0, 99)); // column clamps before the CRLF
        Assert.Equal(11, notes.PositionAt(99, 2)); // line clamps to the last
        Assert.Equal(0, notes.PositionAt(-1, -1));
    }

    [Fact]
    public void PositionAtSnapsOffSurrogateHalves()
    {
        var ui = new TestUi();
        var notes = new EditBox(ui.App, "Notes", "a\U00010437b");
        Assert.Equal(1, notes.PositionAt(0, 2)); // middle of the pair
    }
}

public class SliderTests
{
    [Fact]
    public void AdjustsAndAnnounces()
    {
        var ui = new TestUi();
        var vol = new Slider(ui.App, "Volume", 50, 0, 100, unit: "%");
        var changes = 0;
        vol.Changed += () => changes++;
        vol.Focus();
        Assert.Equal(new[] { "Volume slider 50%" }, ui.Spoken());

        Assert.True(ui.Input(InputKind.MoveRight));
        Assert.Equal(new[] { "51%" }, ui.Spoken());
        Assert.Equal(1, changes);

        // Large steps via Shift+arrow and PageDown.
        ui.Input(InputKind.SelectRight);
        Assert.Equal(new[] { "61%" }, ui.Spoken());
        ui.Raw(KeyCombo.Plain(Key.PageDown));
        Assert.Equal(new[] { "51%" }, ui.Spoken());

        // Home/End jump to the edges.
        ui.Input(InputKind.MoveToLineEnd);
        Assert.Equal(new[] { "100%" }, ui.Spoken());
        Assert.Equal(100, vol.Value);

        // Clamped at max: consumed, re-announced, but no Changed event.
        changes = 0;
        ui.Input(InputKind.MoveRight);
        Assert.Equal(new[] { "100%" }, ui.Spoken());
        Assert.Equal(0, changes);
    }

    [Fact]
    public void ValueSetterSpeaksValueOnlyWhenFocused()
    {
        var ui = new TestUi();
        var progress = new Slider(ui.App, "Progress", 0, 0, 100, unit: "%");
        progress.Focus();
        ui.Drain();

        // A programmatic move speaks like a user-driven one: the value,
        // not "Progress slider 30%".
        progress.Value = 30;
        Assert.Equal(new[] { "30%" }, ui.Spoken());
        Assert.Equal(30, progress.Value);

        // No change (clamped to the same value): silent.
        progress.Value = 200;
        ui.Drain();
        progress.Value = 150;
        Assert.Empty(ui.Spoken());
        Assert.Equal(100, progress.Value);
    }

    [Fact]
    public void ValueSetterSilentWhenUnfocused()
    {
        var ui = new TestUi();
        var progress = new Slider(ui.App, "Progress", 0, 0, 100);
        var other = new Button(ui.App, "Other");
        other.Focus();
        ui.Drain();

        progress.Value = 30;
        Assert.Empty(ui.Spoken());
        Assert.Equal(30, progress.Value);
    }
}

public class TabControlTests
{
    [Fact]
    public void CyclesWithWraparound()
    {
        var ui = new TestUi();
        var tabs = new TabControl(ui.App, "Views", ["Files", "Playlist", "FX"]);
        tabs.Focus();
        Assert.Equal(new[] { "Views tab control Files" }, ui.Spoken());

        ui.Input(InputKind.MoveRight);
        Assert.Equal(new[] { "Playlist" }, ui.Spoken());
        ui.Input(InputKind.MoveRight);
        Assert.Equal(new[] { "FX" }, ui.Spoken());
        ui.Input(InputKind.MoveRight); // wraps
        Assert.Equal(new[] { "Files" }, ui.Spoken());

        ui.Input(InputKind.MoveLeft); // wraps backward
        Assert.Equal(new[] { "FX" }, ui.Spoken());
        Assert.Equal(2, tabs.ActiveIndex);
        Assert.Equal("FX", tabs.ActiveTab);
    }

    [Fact]
    public void ActiveIndexSpeaksTabOnlyWhenFocused()
    {
        var ui = new TestUi();
        var tabs = new TabControl(ui.App, "Views", ["Files", "Playlist", "FX"]);
        tabs.Focus();
        ui.Drain();

        tabs.ActiveIndex = 1;
        Assert.Equal(new[] { "Playlist" }, ui.Spoken());
        Assert.Equal(1, tabs.ActiveIndex);

        // Same tab: silent.
        tabs.ActiveIndex = 1;
        Assert.Empty(ui.Spoken());
    }

    [Fact]
    public void AttachedPanelsFollowTheActiveTab()
    {
        var ui = new TestUi();
        var tabs = new TabControl(ui.App, "Views", ["One", "Two"]);
        var one = new Group(ui.App, "One");
        var two = new Group(ui.App, "Two");
        tabs.AttachPanels(one, two);
        // Attaching hides everything but the active panel.
        Assert.False(one.Hidden);
        Assert.True(two.Hidden);

        tabs.Focus();
        ui.Drain();
        ui.Input(InputKind.MoveRight);
        ui.App.DispatchEvents(); // user-driven switches sync at drain
        Assert.True(one.Hidden);
        Assert.False(two.Hidden);

        // Programmatic switches sync immediately.
        tabs.ActiveIndex = 0;
        Assert.False(one.Hidden);
        Assert.True(two.Hidden);
    }

    [Fact]
    public void AttachPanelsRequiresOnePerTab()
    {
        var ui = new TestUi();
        var tabs = new TabControl(ui.App, "Views", ["One", "Two"]);
        var only = new Group(ui.App, "One");
        Assert.Throws<ArgumentException>(() => tabs.AttachPanels(only));
    }
}

public class ShortcutFieldTests
{
    [Fact]
    public void CapturesAndClears()
    {
        var ui = new TestUi();
        var field = new ShortcutField(ui.App, "Play shortcut");
        var changes = 0;
        field.Changed += () => changes++;
        field.Focus();
        Assert.Equal(new[] { "Play shortcut shortcut field blank" }, ui.Spoken());

        // A raw combo is captured verbatim.
        var ctrlS = KeyCombo.WithCtrl(Key.Char('s'));
        Assert.True(ui.Raw(ctrlS));
        Assert.Equal(new[] { "control s" }, ui.Spoken());
        Assert.Equal(1, changes);
        Assert.Equal(ctrlS, field.Combo);

        // A semantically-mapped input is captured via its combo.
        Assert.True(ui.Input(InputKind.Copy));
        Assert.Equal(new[] { "control c" }, ui.Spoken());

        // Backspace clears.
        Assert.True(ui.Input(InputKind.DeleteBackward));
        Assert.Equal(new[] { "blank" }, ui.Spoken());
        Assert.Null(field.Combo);

        // Tab still leaves the field.
        Assert.True(ui.Input(InputKind.NavigateNext));
    }

    [Fact]
    public void ResistsFrameworkInterception()
    {
        var ui = new TestUi();
        var group = new Group(ui.App, "Options");
        var field = new ShortcutField(group, "Shortcut");
        var other = new Button(ui.App, "Other");
        other.AddShortcut(KeyCombo.WithAlt(Key.Char('o')));
        field.Focus();
        ui.Drain();

        // Alt+Up would be hierarchy navigation; the field captures it.
        ui.Input(InputKind.TreeUp);
        Assert.True(field.IsFocused);
        Assert.Equal(KeyCombo.WithAlt(Key.Up), field.Combo);

        // Alt+O would be a mnemonic jump; the field captures it.
        ui.Input(new InputEvent(InputKind.Shortcut, 'o', 0, Mods.None));
        Assert.True(field.IsFocused);
        Assert.Equal(KeyCombo.WithAlt(Key.Char('o')), field.Combo);

        // Ctrl+Tab arrives as RawKey and is captured (it is host-bindable).
        ui.Raw(KeyCombo.WithCtrl(Key.Tab));
        Assert.Equal(KeyCombo.WithCtrl(Key.Tab), field.Combo);

        // Escape still dismisses (unconsumed here — no cancel widget).
        Assert.False(ui.Input(InputKind.Dismiss));
        Assert.Equal(KeyCombo.WithCtrl(Key.Tab), field.Combo);
    }

    [Fact]
    public void CapturesThePhysicalComboNotTheCanonicalOne()
    {
        var ui = new TestUi();
        var field = new ShortcutField(ui.App, "Shortcut");
        field.Focus();
        ui.Drain();

        // shift+backspace arrives as the same logical kind as the bare
        // clear gesture; the provenance tells the field to capture it.
        Assert.True(ui.Input(
            new InputEvent(InputKind.DeleteBackward, 0, Keys.Backspace, Mods.Shift)));
        Assert.Equal(KeyCombo.WithShift(Key.Backspace), field.Combo);
        Assert.Equal(new[] { "shift backspace" }, ui.Spoken());

        // A shifted letter arrives as a typed uppercase rune carrying its key.
        Assert.True(ui.Input(
            new InputEvent(InputKind.TypeChar, 'Q', Keys.Char('q'), Mods.Shift)));
        Assert.Equal(KeyCombo.WithShift(Key.Char('q')), field.Combo);
        Assert.Equal(new[] { "shift q" }, ui.Spoken());
        Assert.Equal("shift+q", field.Combo?.ToConfigString());

        // Synthetic inputs (no physical origin) fall back to the
        // canonical reverse map.
        Assert.True(ui.Input(InputEvent.Simple(InputKind.DeleteWordBackward)));
        Assert.Equal(KeyCombo.WithCtrl(Key.Backspace), field.Combo);
    }

    [Fact]
    public void ComboSetterSpeaksNewValueWhenFocused()
    {
        var ui = new TestUi();
        var field = new ShortcutField(ui.App, "Play shortcut");
        field.Focus();
        ui.Drain();

        field.Combo = KeyCombo.WithCtrl(Key.Char('p'));
        Assert.Equal(new[] { "control p" }, ui.Spoken());

        field.Combo = null;
        Assert.Equal(new[] { "blank" }, ui.Spoken());
    }
}

public class FilterListBoxTests
{
    private static (TestUi Ui, FilterListBox List) FilterUi()
    {
        var ui = new TestUi();
        var list = new FilterListBox(ui.App, "Commands", ["Save File", "Open Editor", "Quit"]);
        list.Focus();
        ui.Drain();
        return (ui, list);
    }

    [Fact]
    public void FocusAnnouncementCarriesFilterState()
    {
        var ui = new TestUi();
        var list = new FilterListBox(ui.App, "Commands", ["Save File"]);
        list.Focus();
        Assert.Equal(new[] { "Commands list Save File no filter" }, ui.Spoken());
    }

    [Fact]
    public void TypingFiltersAndReports()
    {
        var ui = new TestUi();
        var list = new FilterListBox(
            ui.App, "Commands", ["Save File", "Settings", "Open Editor", "Quit"]);
        var changes = 0;
        list.Changed += () => changes++;
        list.Focus();
        ui.Drain();

        ui.Type('s');
        Assert.Equal(new[] { "Save File 1 of 2" }, ui.Spoken());
        Assert.Equal(1, changes);
        Assert.Equal("s", list.Filter);

        ui.Type('x');
        Assert.Equal(new[] { "no results" }, ui.Spoken());
        Assert.Null(list.SelectedItem);
        // The filter state rides the focus announcement.
        ui.Input(InputKind.SpeakFocus);
        Assert.Equal(new[] { "Commands list empty filter sx" }, ui.Spoken());

        // Backspace restores results.
        ui.Input(InputKind.DeleteBackward);
        Assert.Equal(new[] { "Save File 1 of 2" }, ui.Spoken());
    }

    [Fact]
    public void ArrowsNavigateFilteredSet()
    {
        var (ui, list) = FilterUi();
        ui.Type('e');
        ui.Drain();

        ui.Input(InputKind.MoveDown);
        var spoken = ui.Spoken();
        Assert.Single(spoken);
        Assert.EndsWith("2 of 2", spoken[0]);

        // Bottom boundary repeats with prefix.
        ui.Input(InputKind.MoveDown);
        Assert.StartsWith("bottom, ", ui.Spoken()[0]);

        Assert.NotNull(list.SelectedItem);
    }

    [Fact]
    public void EnterFallsThroughToPrimary()
    {
        var (ui, list) = FilterUi();
        var open = new Button(ui.App, "Open");
        string? chosen = null;
        open.Activated += () => chosen = list.SelectedItem?.Text;
        ui.App.SetPrimary(open);
        ui.Type('q');
        ui.Drain();

        Assert.True(ui.Input(InputKind.Activate));
        ui.Drain();
        Assert.Equal("Quit", chosen);
    }

    [Fact]
    public void ClearFilterResetsQueryAndSpeaksNewSelection()
    {
        var (ui, list) = FilterUi();
        ui.Type('q');
        ui.Drain();
        Assert.Equal("q", list.Filter);

        list.ClearFilter();
        Assert.Equal("", list.Filter);
        Assert.Equal(new[] { "Save File" }, ui.Spoken());
    }
}

public class CheckBoxTests
{
    [Fact]
    public void TogglesOnSpace()
    {
        var ui = new TestUi();
        var wrap = new CheckBox(ui.App, "Word Wrap");
        var toggles = new List<bool>();
        wrap.Toggled += toggles.Add;
        wrap.Focus();
        ui.Drain();

        ui.Type(' ');
        Assert.Equal(new[] { "checked" }, ui.Spoken());
        Assert.Equal(new[] { true }, toggles);
        Assert.True(wrap.Checked);

        ui.Type(' ');
        Assert.Equal(new[] { "not checked" }, ui.Spoken());
        Assert.Equal(new[] { true, false }, toggles);
        Assert.False(wrap.Checked);
    }

    [Fact]
    public void CheckedSetterSpeaksWhenFocusedWithoutToggled()
    {
        var ui = new TestUi();
        var wrap = new CheckBox(ui.App, "Word Wrap");
        var toggles = 0;
        wrap.Toggled += _ => toggles++;
        wrap.Focus();
        ui.Drain();

        wrap.Checked = true;
        Assert.Equal(new[] { "checked" }, ui.Spoken());
        Assert.Equal(0, toggles);

        // Same value: silent. Unfocused: silent.
        wrap.Checked = true;
        Assert.Empty(ui.Spoken());
        var other = new Button(ui.App, "Other");
        other.Focus();
        ui.Drain();
        wrap.Checked = false;
        Assert.Empty(ui.Spoken());
        Assert.False(wrap.Checked);
    }
}

public class CustomWidgetTests
{
    [Fact]
    public void FocusesAndPassesInputThrough()
    {
        var ui = new TestUi();
        var before = new Button(ui.App, "Before");
        var arena = new CustomWidget(ui.App, "Arena");
        var ok = new Button(ui.App, "OK");
        var presses = 0;
        ok.Activated += () => presses++;
        ui.App.SetPrimary(ok);
        before.Focus();
        ui.Drain();

        // In the tab ring, announced by bare name.
        ui.Input(InputKind.NavigateNext);
        Assert.True(arena.IsFocused);
        Assert.Equal(new[] { "Arena" }, ui.Spoken());

        // No built-in behavior: typing and arrows fall through to the
        // host; Enter still reaches the layer's primary.
        Assert.False(ui.Type('q'));
        Assert.False(ui.Input(InputKind.MoveUp));
        Assert.True(ui.Input(InputKind.Activate));
        ui.Drain();
        Assert.Equal(1, presses);
    }

    // Nameless and role-less, the announcement starts at the first
    // present field — no leading space from the missing ones.
    [Fact]
    public void NamelessAnnouncementHasNoLeadingSpace()
    {
        var ui = new TestUi();
        var arena = new CustomWidget(ui.App, "") { Description = "use arrow keys" };
        arena.Disabled = true;
        arena.Focus();
        Assert.Equal(new[] { "unavailable use arrow keys" }, ui.Spoken());
    }
}

public class ClockTests
{
    [Fact]
    public void NowReadsBackTheInjectedClock()
    {
        var ui = new TestUi();
        Assert.Equal(0ul, ui.App.Now);
        ui.App.SetNow(1234);
        Assert.Equal(1234ul, ui.App.Now);
    }

    [Fact]
    public void PreciseNowFollowsTheEngineClockWhenHeadless()
    {
        var ui = new TestUi();
        ui.App.SetNow(2500);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), ui.App.PreciseNow);
    }
}

public class TickerTests
{
    [Fact]
    public void FiresOnClockAdvance()
    {
        var ui = new TestUi();
        var ticker = ui.App.StartTicker(100);
        var ticks = 0;
        ticker.Tick += () => ticks++;

        ui.App.SetNow(50);
        ui.Drain();
        Assert.Equal(0, ticks);

        ui.App.SetNow(100);
        ui.Drain();
        Assert.Equal(1, ticks);

        // A long gap fires once, not once per missed interval.
        ui.App.SetNow(950);
        ui.Drain();
        Assert.Equal(2, ticks);
        // Next interval starts from the late check.
        ui.App.SetNow(1000);
        ui.Drain();
        Assert.Equal(2, ticks);
        ui.App.SetNow(1050);
        ui.Drain();
        Assert.Equal(3, ticks);
    }

    [Fact]
    public void StoppedTickerStopsFiring()
    {
        var ui = new TestUi();
        var a = ui.App.StartTicker(10);
        var b = ui.App.StartTicker(10);
        var aTicks = 0;
        var bTicks = 0;
        a.Tick += () => aTicks++;
        b.Tick += () => bTicks++;
        a.Stop();
        ui.App.SetNow(20);
        ui.Drain();
        Assert.Equal(0, aTicks);
        Assert.Equal(1, bTicks);
    }
}

public class TickTests
{
    [Fact]
    public void TickDrainsQueuedEvents()
    {
        var ui = new TestUi();
        ui.App.Announce("hello");
        Assert.True(ui.App.Tick());
        var announce = Assert.IsType<AccessibilityEvent.Announce>(ui.Reader.Events.Single());
        Assert.Equal("hello", announce.Text);
    }

    [Fact]
    public void QuitTurnsTickFalse()
    {
        var ui = new TestUi();
        Assert.True(ui.App.Tick());
        ui.App.Quit();
        // The final tick still delivers what was queued before Quit.
        ui.App.Announce("goodbye");
        Assert.False(ui.App.Tick());
        Assert.IsType<AccessibilityEvent.Announce>(ui.Reader.Events.Single());
    }

    [Fact]
    public void TickFeedsTheEngineClockFromTheStopwatch()
    {
        var ui = new TestUi();
        // An injected far-future clock is overwritten by real elapsed
        // time, which is nowhere near an hour this early in the test.
        ui.App.SetNow(3_600_000);
        ui.App.Tick();
        Assert.True(ui.App.Now < 3_600_000);
    }

    [Fact]
    public void TickersFireFromTick()
    {
        var ui = new TestUi();
        var ticker = ui.App.StartTicker(1);
        var ticks = 0;
        ticker.Tick += () => ticks++;
        Thread.Sleep(20);
        ui.App.Tick();
        Assert.Equal(1, ticks);
    }
}

public class ReaderTests
{
    [Fact]
    public void EveryReaderHearsEveryAccessibilityEvent()
    {
        var ui = new TestUi();
        var second = new TestReader();
        ui.App.AddReader(second);
        var save = new Button(ui.App, "Save");
        save.Focus();
        ui.App.DispatchEvents();

        Assert.Single(ui.Reader.Events);
        Assert.Single(second.Events);
        var focused = Assert.IsType<AccessibilityEvent.Focused>(second.Events[0]);
        Assert.Same(save, focused.Widget);
        Assert.Equal("Save", focused.Info.Name);
        Assert.Equal("button", focused.Info.Role);

        Assert.True(ui.App.RemoveReader(second));
        ui.App.Announce("hello");
        ui.App.DispatchEvents();
        Assert.Single(second.Events);
    }

    [Fact]
    public void InterruptReachesReaders()
    {
        var ui = new TestUi();
        ui.App.Interrupt();
        Assert.Equal(1, ui.Reader.Interrupts);
    }

    [Fact]
    public void AppAnnouncesCarryNoSource()
    {
        var ui = new TestUi();
        ui.App.Announce("hello");
        ui.App.DispatchEvents();
        var announce = Assert.IsType<AccessibilityEvent.Announce>(ui.Reader.Events.Single());
        Assert.Null(announce.Source);
    }

    [Fact]
    public void CheckBoxTogglesAreStructured()
    {
        var ui = new TestUi();
        var mute = new CheckBox(ui.App, "Mute");
        mute.Focus();
        ui.Drain();

        ui.Type(' ');
        ui.App.DispatchEvents();
        var toggle = Assert.IsType<AccessibilityEvent.Toggle>(
            ui.Reader.Events.Single(e => e is AccessibilityEvent.Toggle));
        Assert.Same(mute, toggle.Widget);
        Assert.True(toggle.Checked);
    }

    [Fact]
    public void EmptyEditorFeedbackIsStructured()
    {
        var ui = new TestUi();
        var notes = new EditBox(ui.App, "Notes");
        notes.Focus();
        ui.Drain();

        ui.Input(InputKind.DeleteBackward);
        ui.App.DispatchEvents();
        var noop = Assert.IsType<AccessibilityEvent.EditNoop>(ui.Reader.Events.Single());
        Assert.Same(notes, noop.Widget);
        Assert.Equal(EditNoopKind.NothingToDelete, noop.Kind);
    }

    [Fact]
    public void StructuredEventsCarryWidgetReferences()
    {
        var ui = new TestUi();
        var files = new ListBox(ui.App, "Files", ["a", "b"], numbered: true);
        files.Focus();
        ui.Drain();

        ui.Input(InputKind.MoveDown);
        ui.App.DispatchEvents();
        var nav = Assert.IsType<AccessibilityEvent.ItemNav>(
            ui.Reader.Events.Single(e => e is AccessibilityEvent.ItemNav));
        Assert.Same(files, nav.Widget);
        Assert.Equal("b", nav.Item);
        Assert.Equal((1, 2), nav.Position);
    }
}

/// <summary>The behavior-authoring forcing function: a two-dimensional
/// grid widget written entirely from the public base class. Arrows move
/// the cell cursor, boundary hits are announced, the label mirrors the
/// cell, and the widget reserves its arrows for bind-dialog warnings.</summary>
public class WidgetAuthoringTests
{
    private sealed class GridWidget : Widget
    {
        private readonly string[][] _cells;
        private int _row;
        private int _col;

        public GridWidget(IWidgetContainer parent, string name, string[][] cells)
            : base(parent, name, roleText: "grid")
        {
            _cells = cells;
        }

        public string Cell => _cells[_row][_col];

        protected internal override string ValueText => Cell;

        protected internal override string StateText => $"row {_row + 1} column {_col + 1}";

        public event Action<string>? CellChosen;

        public override bool ReservesKey(KeyCombo combo) =>
            !combo.Ctrl && !combo.Alt && !combo.Shift
            && (combo.Key == Key.Up || combo.Key == Key.Down
                || combo.Key == Key.Left || combo.Key == Key.Right
                || combo.Key == Key.Enter);

        protected override bool OnInput(in InputEvent input)
        {
            var (dr, dc) = input.Kind switch
            {
                InputKind.MoveUp => (-1, 0),
                InputKind.MoveDown => (1, 0),
                InputKind.MoveLeft => (0, -1),
                InputKind.MoveRight => (0, 1),
                _ => (0, 0),
            };
            if ((dr, dc) != (0, 0))
            {
                var row = Math.Clamp(_row + dr, 0, _cells.Length - 1);
                var col = Math.Clamp(_col + dc, 0, _cells[0].Length - 1);
                Boundary? boundary = (row, col) == (_row, _col)
                    ? dr < 0 ? Boundary.Top
                        : dr > 0 ? Boundary.Bottom
                        : dc < 0 ? Boundary.Left : Boundary.Right
                    : null;
                (_row, _col) = (row, col);
                AnnounceItem(boundary is Boundary.Left or Boundary.Right
                    ? $"edge, {Cell}"
                    : Cell, null, boundary is Boundary.Top or Boundary.Bottom ? boundary : null);
                if (boundary is null)
                    PostChanged();
                return true;
            }
            if (input.Kind == InputKind.Activate)
            {
                var cell = Cell;
                Post(() => CellChosen?.Invoke(cell));
                return true;
            }
            return false;
        }
    }

    private static (TestUi Ui, GridWidget Grid) GridUi()
    {
        var ui = new TestUi();
        var grid = new GridWidget(
            ui.App, "Board",
            [
                ["a1", "b1"],
                ["a2", "b2"],
            ]);
        grid.Focus();
        ui.Drain();
        return (ui, grid);
    }

    [Fact]
    public void GridIsAFullCitizen()
    {
        var (ui, grid) = GridUi();

        // Focus announcement composes from the golden six it maintains.
        ui.Input(InputKind.SpeakFocus);
        Assert.Equal(new[] { "Board grid a1 row 1 column 1" }, ui.Spoken());

        // Arrows navigate cells and announce them.
        ui.Input(InputKind.MoveRight);
        Assert.Equal(new[] { "b1" }, ui.Spoken());
        ui.Input(InputKind.MoveDown);
        Assert.Equal(new[] { "b2" }, ui.Spoken());
        Assert.Equal("b2", grid.Cell);

        // Boundary hits announce without moving.
        ui.Input(InputKind.MoveDown);
        Assert.Equal(new[] { "bottom, b2" }, ui.Spoken());
        Assert.Equal("b2", grid.Cell);

        // Its own deferred event delivers on drain.
        string? chosen = null;
        grid.CellChosen += cell => chosen = cell;
        Assert.True(ui.Input(InputKind.Activate));
        ui.Drain();
        Assert.Equal("b2", chosen);

        // It reserves its interaction keys for bind-dialog warnings...
        Assert.True(grid.ReservesKey(KeyCombo.Plain(Key.Up)));
        Assert.True(grid.ReservesKey(KeyCombo.Plain(Key.Enter)));
        Assert.False(grid.ReservesKey(KeyCombo.WithCtrl(Key.Char('s'))));

        // ...and unclaimed input still falls through to the framework.
        var after = new Button(ui.App, "After");
        ui.Drain();
        ui.Input(InputKind.NavigateNext);
        Assert.True(after.IsFocused);
        Assert.Equal(new[] { "After button" }, ui.Spoken());
    }
}

/// <summary>Focused events carry why focus moved (FocusCause), so a
/// reader can treat user movement differently from focus that came to
/// the user — an earcon reader clicks on navigation but not on a dialog
/// opening.</summary>
public class FocusCauseTests
{
    /// <summary>Deliver queued output and return the causes of the
    /// Focused events heard since the last call, in order.</summary>
    private static List<FocusCause> Causes(TestUi ui)
    {
        ui.App.DispatchEvents();
        var causes = ui.Reader.Events
            .OfType<AccessibilityEvent.Focused>()
            .Select(f => f.Cause)
            .ToList();
        ui.Reader.Events.Clear();
        return causes;
    }

    [Fact]
    public void UserNavigationIsAttributed()
    {
        var ui = new TestUi();
        _ = new Button(ui.App, "Save");
        var options = new Group(ui.App, "Options");
        var wrap = new CheckBox(options, "Word Wrap");
        ui.App.EnsureFocus();
        Assert.Equal(new[] { FocusCause.Programmatic }, Causes(ui));

        ui.Input(InputKind.NavigateNext);
        Assert.True(wrap.IsFocused);
        Assert.Equal(new[] { FocusCause.UserNavigation }, Causes(ui));

        ui.Input(InputKind.TreeUp);
        Assert.Equal(new[] { FocusCause.UserNavigation }, Causes(ui));
    }

    [Fact]
    public void ShortcutJumpIsAttributed()
    {
        var ui = new TestUi();
        var save = new Button(ui.App, "Save");
        var target = new Button(ui.App, "Target");
        target.AddShortcut(KeyCombo.WithCtrl(Key.Char('j')));
        save.Focus();
        ui.Drain();

        ui.Raw(KeyCombo.WithCtrl(Key.Char('j')));
        Assert.True(target.IsFocused);
        Assert.Equal(new[] { FocusCause.Shortcut }, Causes(ui));
    }

    [Fact]
    public void ProgrammaticFocusAndReannouncementAreAttributed()
    {
        var ui = new TestUi();
        _ = new Button(ui.App, "Save");
        var other = new Button(ui.App, "Other");
        ui.Drain();

        other.Focus();
        Assert.Equal(new[] { FocusCause.Programmatic }, Causes(ui));

        ui.Input(InputKind.SpeakFocus);
        Assert.Equal(new[] { FocusCause.Reannounce }, Causes(ui));
    }

    [Fact]
    public void RecoveryIsAttributed()
    {
        var ui = new TestUi();
        _ = new Button(ui.App, "Save");
        var doomed = new Button(ui.App, "Doomed");
        doomed.Focus();
        ui.Drain();

        doomed.Remove();
        Assert.Equal(new[] { FocusCause.Recovery }, Causes(ui));
    }

    [Fact]
    public void DialogTransitionsNeverReadAsUserNavigation()
    {
        var ui = new TestUi();
        var open = new Button(ui.App, "Open");
        open.Focus();
        ui.Drain();

        var dialog = ui.App.OpenDialog();
        _ = new Label(dialog, "Prompt");
        _ = new Button(dialog, "OK");
        dialog.AnnounceOpened();
        var causes = Causes(ui);
        Assert.NotEmpty(causes);
        Assert.DoesNotContain(FocusCause.UserNavigation, causes);

        dialog.Close();
        Assert.Equal(new[] { FocusCause.LayerRestore }, Causes(ui));
    }
}
