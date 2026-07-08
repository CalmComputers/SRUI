using Srui;
using Srui.Core;
using Xunit;

namespace Srui.Net.Tests;

public class CoreUiTests
{
    /// <summary>Render every accessibility event in a drained batch, in order.</summary>
    private static List<string> Spoken(List<CoreEvent> events) =>
        events
            .OfType<CoreEvent.Acc>()
            .Select(acc => SpeechRenderer.RenderEvent(acc.Event))
            .Where(s => s is not null)
            .Select(s => s!)
            .ToList();

    private static bool ContainsActivated(List<CoreEvent> events, NodeId node) =>
        events.Contains(new CoreEvent.Activated(node));

    private static bool AnyActivated(List<CoreEvent> events) =>
        events.Any(ev => ev is CoreEvent.Activated);

    private static InputEvent Simple(InputKind kind) => InputEvent.Simple(kind);

    private static InputEvent Raw(KeyCombo combo)
    {
        var (key, mods) = combo.ToFlat();
        return InputEvent.RawKey(key, mods);
    }

    private static (CoreUi Ui, NodeId Save, NodeId Options, NodeId Wrap) DemoUi()
    {
        var ui = new CoreUi();
        var save = ui.Button(NodeId.None, "Save");
        var options = ui.Group(NodeId.None, "Options");
        var wrap = ui.Checkbox(options, "Word Wrap", false);
        return (ui, save, options, wrap);
    }

    [Fact]
    public void EnsureFocusAnnouncesFirstFocusable()
    {
        var (ui, save, _, _) = DemoUi();
        Assert.True(ui.EnsureFocus());
        Assert.Equal(save, ui.Focus);
        Assert.Equal(new[] { "Save button" }, Spoken(ui.DrainEvents()));
    }

    [Fact]
    public void FirstTabEstablishesFocusWithoutMoving()
    {
        var (ui, save, _, _) = DemoUi();
        Assert.True(ui.HandleInput(Simple(InputKind.NavigateNext)));
        Assert.Equal(save, ui.Focus);
    }

    [Fact]
    public void TabMovesAndAnnounces()
    {
        var (ui, _, _, wrap) = DemoUi();
        ui.EnsureFocus();
        ui.DrainEvents();

        ui.HandleInput(Simple(InputKind.NavigateNext));
        Assert.Equal(wrap, ui.Focus);
        Assert.Equal(new[] { "Word Wrap check box not checked" }, Spoken(ui.DrainEvents()));
    }

    [Fact]
    public void CoalescingKeepsOnlySettledFocus()
    {
        var (ui, save, _, _) = DemoUi();
        ui.EnsureFocus();
        ui.HandleInput(Simple(InputKind.NavigateNext));
        ui.HandleInput(Simple(InputKind.NavigateNext)); // wraps back to Save
        Assert.Equal(save, ui.Focus);
        Assert.Equal(new[] { "Save button" }, Spoken(ui.DrainEvents()));
    }

    [Fact]
    public void ButtonActivatesOnEnterAndSpace()
    {
        var (ui, save, _, _) = DemoUi();
        ui.SetFocus(save);
        ui.DrainEvents();

        Assert.True(ui.HandleInput(Simple(InputKind.Activate)));
        Assert.True(ui.HandleInput(InputEvent.TypeChar(' ')));
        var activations = ui.DrainEvents()
            .Count(ev => ev is CoreEvent.Activated(var node) && node == save);
        Assert.Equal(2, activations);
    }

    [Fact]
    public void CheckboxTogglesOnSpace()
    {
        var (ui, _, _, wrap) = DemoUi();
        ui.SetFocus(wrap);
        ui.DrainEvents();

        ui.HandleInput(InputEvent.TypeChar(' '));
        var events = ui.DrainEvents();
        Assert.Contains(new CoreEvent.Toggled(wrap, true), events);
        Assert.Equal(new[] { "checked" }, Spoken(events));
        Assert.True(ui.Widget<CheckBoxBehavior>(wrap)!.Checked);
        Assert.Equal("checked", ui.Label(wrap)!.Value);

        ui.HandleInput(InputEvent.TypeChar(' '));
        Assert.Equal(new[] { "not checked" }, Spoken(ui.DrainEvents()));
        Assert.False(ui.Widget<CheckBoxBehavior>(wrap)!.Checked);
    }

    [Fact]
    public void EnterOnCheckboxFallsThroughToPrimary()
    {
        var ui = new CoreUi();
        var wrap = ui.Checkbox(NodeId.None, "Word Wrap", false);
        var ok = ui.Button(NodeId.None, "OK");
        ui.SetPrimary(ok);
        ui.SetFocus(wrap);
        ui.DrainEvents();

        Assert.True(ui.HandleInput(Simple(InputKind.Activate)));
        var events = ui.DrainEvents();
        Assert.True(ContainsActivated(events, ok));
        Assert.False(ui.Widget<CheckBoxBehavior>(wrap)!.Checked);
    }

    [Fact]
    public void HiddenPrimaryDoesNotActivate()
    {
        var ui = new CoreUi();
        var wrap = ui.Checkbox(NodeId.None, "Word Wrap", false);
        var ok = ui.Button(NodeId.None, "OK");
        ui.SetPrimary(ok);
        ui.SetHidden(ok, true);
        ui.SetFocus(wrap);
        ui.DrainEvents();

        Assert.False(ui.HandleInput(Simple(InputKind.Activate)));
        Assert.False(AnyActivated(ui.DrainEvents()));
    }

    [Fact]
    public void DisabledPrimaryDoesNotActivate()
    {
        var ui = new CoreUi();
        var wrap = ui.Checkbox(NodeId.None, "Word Wrap", false);
        var ok = ui.Button(NodeId.None, "OK");
        ui.SetPrimary(ok);
        ui.SetDisabled(ok, true);
        ui.SetFocus(wrap);
        ui.DrainEvents();

        Assert.False(ui.HandleInput(Simple(InputKind.Activate)));
        Assert.False(AnyActivated(ui.DrainEvents()));
    }

    [Fact]
    public void PrimaryUnderHiddenAncestorDoesNotActivate()
    {
        var ui = new CoreUi();
        var wrap = ui.Checkbox(NodeId.None, "Word Wrap", false);
        var panel = ui.Group(NodeId.None, "Panel");
        var ok = ui.Button(panel, "OK");
        ui.SetPrimary(ok);
        ui.SetHidden(panel, true);
        ui.SetFocus(wrap);
        ui.DrainEvents();

        Assert.False(ui.HandleInput(Simple(InputKind.Activate)));
        Assert.False(AnyActivated(ui.DrainEvents()));
    }

    [Fact]
    public void DisabledCancelLeavesDismissUnconsumed()
    {
        var ui = new CoreUi();
        ui.Button(NodeId.None, "Body");
        var cancel = ui.Button(NodeId.None, "Cancel");
        ui.SetCancel(cancel);
        ui.SetDisabled(cancel, true);
        ui.EnsureFocus();
        ui.DrainEvents();

        Assert.False(ui.HandleInput(Simple(InputKind.Dismiss)));
        Assert.False(AnyActivated(ui.DrainEvents()));
    }

    [Fact]
    public void DismissActivatesCancel()
    {
        var ui = new CoreUi();
        ui.Button(NodeId.None, "Body");
        var cancel = ui.Button(NodeId.None, "Cancel");
        ui.SetCancel(cancel);
        ui.EnsureFocus();
        ui.DrainEvents();

        Assert.True(ui.HandleInput(Simple(InputKind.Dismiss)));
        Assert.True(ContainsActivated(ui.DrainEvents(), cancel));
    }

    [Fact]
    public void DismissUnconsumedWithoutCancel()
    {
        var (ui, _, _, _) = DemoUi();
        ui.EnsureFocus();
        Assert.False(ui.HandleInput(Simple(InputKind.Dismiss)));
    }

    [Fact]
    public void MnemonicJumpsToWidget()
    {
        var (ui, _, _, wrap) = DemoUi();
        ui.AddShortcut(wrap, KeyCombo.WithAlt(Key.Char('w')), ShortcutAction.Jump);
        ui.EnsureFocus();
        ui.DrainEvents();

        Assert.True(ui.HandleInput(new InputEvent(InputKind.Shortcut, 'w', 0, Mods.None)));
        Assert.Equal(wrap, ui.Focus);
    }

    [Fact]
    public void ShortcutJumpFocusesAndAnnounces()
    {
        var (ui, save, _, wrap) = DemoUi();
        ui.AddShortcut(wrap, KeyCombo.WithCtrl(Key.Char('w')), ShortcutAction.Jump);
        ui.SetFocus(save);
        ui.DrainEvents();

        Assert.True(ui.HandleInput(Raw(KeyCombo.WithCtrl(Key.Char('w')))));
        Assert.Equal(wrap, ui.Focus);
        Assert.Equal(
            new[] { "Word Wrap check box not checked control w" },
            Spoken(ui.DrainEvents()));
    }

    [Fact]
    public void ShortcutActivateFiresWithoutMovingFocus()
    {
        var (ui, save, _, wrap) = DemoUi();
        ui.AddShortcut(save, KeyCombo.WithCtrl(Key.Char('g')), ShortcutAction.Activate);
        ui.SetFocus(wrap);
        ui.DrainEvents();

        Assert.True(ui.HandleInput(Raw(KeyCombo.WithCtrl(Key.Char('g')))));
        var events = ui.DrainEvents();
        Assert.True(ContainsActivated(events, save));
        Assert.Equal(wrap, ui.Focus);
        Assert.Empty(Spoken(events));
    }

    [Fact]
    public void ShortcutJumpAndActivateDoesBoth()
    {
        var (ui, save, _, wrap) = DemoUi();
        ui.AddShortcut(save, KeyCombo.WithCtrl(Key.Char('s')), ShortcutAction.JumpAndActivate);
        ui.SetFocus(wrap);
        ui.DrainEvents();

        Assert.True(ui.HandleInput(Raw(KeyCombo.WithCtrl(Key.Char('s')))));
        var events = ui.DrainEvents();
        Assert.Equal(save, ui.Focus);
        Assert.True(ContainsActivated(events, save));
        Assert.Equal(new[] { "Save button control s" }, Spoken(events));
    }

    [Fact]
    public void ShortcutOnUnreachableWidgetIsInert()
    {
        var (ui, save, options, wrap) = DemoUi();
        ui.AddShortcut(wrap, KeyCombo.WithCtrl(Key.Char('w')), ShortcutAction.Jump);
        ui.SetFocus(save);
        ui.DrainEvents();

        var ctrlW = Raw(KeyCombo.WithCtrl(Key.Char('w')));

        ui.SetDisabled(wrap, true);
        Assert.False(ui.HandleInput(ctrlW));
        Assert.Equal(save, ui.Focus);

        ui.SetDisabled(wrap, false);
        ui.SetHidden(options, true);
        Assert.False(ui.HandleInput(ctrlW));
        Assert.Equal(save, ui.Focus);
    }

    [Fact]
    public void FocusedWidgetBeatsShortcut()
    {
        var ui = new CoreUi();
        var notes = ui.Editbox(NodeId.None, "Notes", "", false);
        var other = ui.Button(NodeId.None, "Other");
        ui.AddShortcut(other, KeyCombo.Plain(Key.Char('x')), ShortcutAction.Jump);
        ui.SetFocus(other);
        ui.DrainEvents();
        // Jumping to the already-focused widget is a consumed no-op.
        Assert.True(ui.HandleInput(InputEvent.TypeChar('x')));
        Assert.Equal(other, ui.Focus);

        // ...but an edit box consumes the keystroke first.
        ui.SetFocus(notes);
        ui.DrainEvents();
        Assert.True(ui.HandleInput(InputEvent.TypeChar('x')));
        Assert.Equal(notes, ui.Focus);
        Assert.Equal("x", ui.Widget<EditBoxBehavior>(notes)!.Text);
    }

    [Fact]
    public void FirstClaimantInTreeOrderWins()
    {
        var ui = new CoreUi();
        var first = ui.Button(NodeId.None, "First");
        var second = ui.Button(NodeId.None, "Second");
        var combo = KeyCombo.WithCtrl(Key.Char('k'));
        ui.AddShortcut(second, combo, ShortcutAction.Jump);
        ui.AddShortcut(first, combo, ShortcutAction.Jump);
        ui.SetFocus(second);
        ui.DrainEvents();

        Assert.True(ui.HandleInput(Raw(combo)));
        Assert.Equal(first, ui.Focus);

        // When the first claimant becomes unreachable, the next one wins.
        ui.SetHidden(first, true);
        ui.DrainEvents();
        ui.SetFocus(second);
        Assert.True(ui.HandleInput(Raw(combo)));
        Assert.Equal(second, ui.Focus);
    }

    [Fact]
    public void ShortcutsInLowerLayersAreInert()
    {
        var ui = new CoreUi();
        var save = ui.Button(NodeId.None, "Save");
        var combo = KeyCombo.WithCtrl(Key.Char('s'));
        ui.AddShortcut(save, combo, ShortcutAction.Activate);
        ui.EnsureFocus();
        ui.DrainEvents();

        ui.PushLayer();
        ui.Button(NodeId.None, "Confirm");
        ui.EnsureFocus();
        ui.DrainEvents();
        Assert.False(ui.HandleInput(Raw(combo)));
        Assert.False(AnyActivated(ui.DrainEvents()));

        ui.PopLayer();
        ui.DrainEvents();
        Assert.True(ui.HandleInput(Raw(combo)));
        Assert.True(ContainsActivated(ui.DrainEvents(), save));
    }

    [Fact]
    public void ClearShortcutsRemovesBindingsAndAnnouncement()
    {
        var (ui, save, _, _) = DemoUi();
        ui.AddShortcut(save, KeyCombo.WithCtrl(Key.Char('s')), ShortcutAction.Activate);
        ui.ClearShortcuts(save);
        ui.EnsureFocus();

        Assert.Equal(new[] { "Save button" }, Spoken(ui.DrainEvents()));
        Assert.False(ui.HandleInput(Raw(KeyCombo.WithCtrl(Key.Char('s')))));
    }

    [Fact]
    public void SpeakFocusReannounces()
    {
        var (ui, _, _, _) = DemoUi();
        ui.EnsureFocus();
        ui.DrainEvents();

        ui.HandleInput(Simple(InputKind.SpeakFocus));
        Assert.Equal(new[] { "Save button" }, Spoken(ui.DrainEvents()));
    }

    [Fact]
    public void FocusRecoversWhenFocusedNodeRemoved()
    {
        var (ui, _, _, wrap) = DemoUi();
        ui.SetFocus(wrap);
        ui.DrainEvents();

        ui.Remove(wrap);
        Assert.NotEqual(NodeId.None, ui.Focus);
        Assert.NotEqual(wrap, ui.Focus);
        Assert.Single(Spoken(ui.DrainEvents()));
    }

    [Fact]
    public void FocusRecoversWhenFocusedSubtreeRemoved()
    {
        var (ui, save, options, wrap) = DemoUi();
        ui.SetFocus(wrap);
        ui.DrainEvents();

        ui.Remove(options);
        Assert.Equal(save, ui.Focus);
    }

    [Fact]
    public void FocusMemoryRestoresLastChildOnReentry()
    {
        var ui = new CoreUi();
        var group = ui.Group(NodeId.None, "Options");
        ui.Checkbox(group, "First", false);
        var second = ui.Checkbox(group, "Second", false);

        ui.SetFocus(second);
        ui.HandleInput(Simple(InputKind.TreeUp));
        Assert.Equal(group, ui.Focus);
        ui.HandleInput(Simple(InputKind.TreeDown));
        Assert.Equal(second, ui.Focus);
    }

    [Fact]
    public void LayerPopRestoresAndAnnouncesFocus()
    {
        var (ui, save, _, _) = DemoUi();
        ui.SetFocus(save);
        ui.DrainEvents();

        ui.PushLayer();
        var confirm = ui.Button(NodeId.None, "Confirm");
        ui.SetFocus(confirm);
        ui.DrainEvents();

        ui.PopLayer();
        Assert.Equal(save, ui.Focus);
        Assert.Equal(new[] { "Save button" }, Spoken(ui.DrainEvents()));
    }

    [Fact]
    public void UpdateLabelReannouncesFocusedNodeOnly()
    {
        var (ui, save, _, wrap) = DemoUi();
        ui.SetFocus(save);
        ui.DrainEvents();

        // Unfocused node: silent.
        ui.UpdateLabel(wrap, label => label.Description = "wraps long lines");
        Assert.Empty(ui.DrainEvents());

        // Focused node: re-announced.
        ui.UpdateLabel(save, label => label.Description = "saves the file");
        Assert.Equal(new[] { "Save button saves the file" }, Spoken(ui.DrainEvents()));

        // No-op mutation: silent.
        ui.UpdateLabel(save, _ => { });
        Assert.Empty(ui.DrainEvents());
    }

    [Fact]
    public void ReannounceWithContextCollectsPrecedingLabels()
    {
        var ui = new CoreUi();
        ui.PushLayer();
        ui.TextLabel(NodeId.None, "Delete 3 files?");
        var yes = ui.Button(NodeId.None, "Yes");
        ui.SetFocus(yes);
        ui.DrainEvents();

        ui.ReannounceWithContext();
        Assert.Equal(new[] { "Delete 3 files? Yes button" }, Spoken(ui.DrainEvents()));
    }

    // ── ListBox ──

    private static (CoreUi Ui, NodeId Files) ListUi(bool numbered)
    {
        var ui = new CoreUi();
        var files = ui.Listbox(
            NodeId.None, "Files",
            new List<string> { "alpha.txt", "bravo.txt", "charlie.txt" },
            numbered);
        ui.SetFocus(files);
        ui.DrainEvents();
        return (ui, files);
    }

    [Fact]
    public void ListboxFocusAnnouncementIncludesValueAndPosition()
    {
        var ui = new CoreUi();
        var files = ui.Listbox(
            NodeId.None, "Files",
            new List<string> { "alpha.txt", "bravo.txt", "charlie.txt" },
            true);
        ui.SetFocus(files);
        Assert.Equal(new[] { "Files list alpha.txt 1 of 3" }, Spoken(ui.DrainEvents()));
    }

    [Fact]
    public void ListboxArrowsAnnounceItems()
    {
        var (ui, files) = ListUi(true);

        Assert.True(ui.HandleInput(Simple(InputKind.MoveDown)));
        var events = ui.DrainEvents();
        Assert.Equal(new[] { "bravo.txt 2 of 3" }, Spoken(events));
        Assert.Contains(new CoreEvent.Changed(files), events);
        Assert.Equal(1, ui.Widget<ListBoxBehavior>(files)!.Selected);
        Assert.Equal("bravo.txt", ui.Label(files)!.Value);
    }

    [Fact]
    public void ListboxUnnumberedAnnouncesBareItem()
    {
        var (ui, _) = ListUi(false);
        ui.HandleInput(Simple(InputKind.MoveDown));
        Assert.Equal(new[] { "bravo.txt" }, Spoken(ui.DrainEvents()));
    }

    [Fact]
    public void ListboxBoundariesAnnounceWithoutMoving()
    {
        var (ui, files) = ListUi(true);

        ui.HandleInput(Simple(InputKind.MoveUp));
        Assert.Equal(new[] { "top, alpha.txt 1 of 3" }, Spoken(ui.DrainEvents()));
        Assert.Equal(0, ui.Widget<ListBoxBehavior>(files)!.Selected);

        ui.HandleInput(Simple(InputKind.MoveToDocEnd));
        ui.DrainEvents();
        ui.HandleInput(Simple(InputKind.MoveDown));
        Assert.Equal(new[] { "bottom, charlie.txt 3 of 3" }, Spoken(ui.DrainEvents()));
    }

    [Fact]
    public void ListboxHomeEndJump()
    {
        var (ui, files) = ListUi(true);

        ui.HandleInput(Simple(InputKind.MoveToDocEnd));
        Assert.Equal(2, ui.Widget<ListBoxBehavior>(files)!.Selected);
        Assert.Equal(new[] { "charlie.txt 3 of 3" }, Spoken(ui.DrainEvents()));

        ui.HandleInput(Simple(InputKind.MoveToDocStart));
        Assert.Equal(0, ui.Widget<ListBoxBehavior>(files)!.Selected);
    }

    [Fact]
    public void EnterInListActivatesPrimary()
    {
        var (ui, files) = ListUi(true);
        var open = ui.Button(NodeId.None, "Open");
        ui.SetPrimary(open);
        ui.HandleInput(Simple(InputKind.MoveDown));
        ui.DrainEvents();

        Assert.True(ui.HandleInput(Simple(InputKind.Activate)));
        Assert.True(ContainsActivated(ui.DrainEvents(), open));
        Assert.Equal("bravo.txt", ui.Widget<ListBoxBehavior>(files)!.SelectedItem);
    }

    [Fact]
    public void EnterInListUnconsumedWithoutPrimary()
    {
        var (ui, _) = ListUi(true);
        Assert.False(ui.HandleInput(Simple(InputKind.Activate)));
        Assert.Empty(ui.DrainEvents());
    }

    [Fact]
    public void ListboxTypeaheadFirstLetterCycles()
    {
        var ui = new CoreUi();
        var files = ui.Listbox(
            NodeId.None, "Files",
            new List<string> { "apple", "banana", "avocado" },
            false);
        ui.SetFocus(files);
        ui.DrainEvents();

        // 'a' from apple → next item starting with 'a' (wraps past banana).
        ui.SetNow(1000);
        ui.HandleInput(InputEvent.TypeChar('a'));
        Assert.Equal("avocado", ui.Widget<ListBoxBehavior>(files)!.SelectedItem);
        Assert.Equal(new[] { "avocado" }, Spoken(ui.DrainEvents()));

        // Repeated 'a' cycles onward: avocado → apple.
        ui.SetNow(1100);
        ui.HandleInput(InputEvent.TypeChar('a'));
        Assert.Equal("apple", ui.Widget<ListBoxBehavior>(files)!.SelectedItem);
    }

    [Fact]
    public void ListboxTypeaheadPrefixSearch()
    {
        var ui = new CoreUi();
        var files = ui.Listbox(
            NodeId.None, "Files",
            new List<string> { "banana", "berry", "cherry" },
            false);
        ui.SetFocus(files);
        ui.DrainEvents();

        ui.SetNow(1000);
        ui.HandleInput(InputEvent.TypeChar('b'));
        // From banana, 'b' cycles to the NEXT b-item: berry.
        Assert.Equal("berry", ui.Widget<ListBoxBehavior>(files)!.SelectedItem);
        ui.SetNow(1100);
        ui.HandleInput(InputEvent.TypeChar('e'));
        // Buffer "be" → prefix search keeps berry.
        Assert.Equal("berry", ui.Widget<ListBoxBehavior>(files)!.SelectedItem);
    }

    [Fact]
    public void ListboxTypeaheadTimesOut()
    {
        var ui = new CoreUi();
        var files = ui.Listbox(
            NodeId.None, "Files",
            new List<string> { "banana", "berry", "cat" },
            false);
        ui.SetFocus(files);
        ui.DrainEvents();

        ui.SetNow(1000);
        ui.HandleInput(InputEvent.TypeChar('b'));
        Assert.Equal("berry", ui.Widget<ListBoxBehavior>(files)!.SelectedItem);

        // 500ms later the buffer has expired: 'c' is a fresh first letter.
        ui.SetNow(1500);
        ui.HandleInput(InputEvent.TypeChar('c'));
        Assert.Equal("cat", ui.Widget<ListBoxBehavior>(files)!.SelectedItem);
    }

    [Fact]
    public void ListboxEmptyConsumesNothing()
    {
        var ui = new CoreUi();
        var files = ui.Listbox(NodeId.None, "Files", new List<string>(), true);
        ui.SetFocus(files);
        Assert.Equal("empty", ui.Label(files)!.Value);
        ui.DrainEvents();

        Assert.False(ui.HandleInput(Simple(InputKind.Activate)));
        Assert.Empty(ui.DrainEvents());
    }

    [Fact]
    public void SetListItemsClampsAndReannouncesWhenFocused()
    {
        var (ui, files) = ListUi(true);
        ui.HandleInput(Simple(InputKind.MoveToDocEnd));
        ui.DrainEvents();

        ui.SetListItems(files, new List<string> { "only.txt" });
        Assert.Equal(0, ui.Widget<ListBoxBehavior>(files)!.Selected);
        Assert.Equal(new[] { "Files list only.txt 1 of 1" }, Spoken(ui.DrainEvents()));
    }

    [Fact]
    public void SetListItemsSilentWhenUnfocused()
    {
        var ui = new CoreUi();
        var files = ui.Listbox(NodeId.None, "Files", new List<string> { "a" }, false);
        var other = ui.Button(NodeId.None, "Other");
        ui.SetFocus(other);
        ui.DrainEvents();

        ui.SetListItems(files, new List<string> { "b" });
        Assert.Empty(ui.DrainEvents());
    }

    [Fact]
    public void SetListSelectedSpeaksItemOnlyWhenFocused()
    {
        var (ui, files) = ListUi(true);

        ui.SetListSelected(files, 1);
        Assert.Equal(new[] { "bravo.txt 2 of 3" }, Spoken(ui.DrainEvents()));
        Assert.Equal(1, ui.Widget<ListBoxBehavior>(files)!.Selected);

        // Same index again: silent.
        ui.SetListSelected(files, 1);
        Assert.Empty(ui.DrainEvents());
    }

    // ── EditBox ──

    [Fact]
    public void EditboxFocusAnnouncement()
    {
        var ui = new CoreUi();
        var notes = ui.Editbox(NodeId.None, "Notes", "", false);
        ui.SetFocus(notes);
        Assert.Equal(new[] { "Notes edit blank" }, Spoken(ui.DrainEvents()));
    }

    [Fact]
    public void EditboxTypingEchoesAndUpdatesState()
    {
        var ui = new CoreUi();
        var notes = ui.Editbox(NodeId.None, "Notes", "", false);
        ui.SetFocus(notes);
        ui.DrainEvents();

        Assert.True(ui.HandleInput(InputEvent.TypeChar('h')));
        var events = ui.DrainEvents();
        Assert.Equal(new[] { "h" }, Spoken(events));
        Assert.Contains(new CoreEvent.Changed(notes), events);

        ui.HandleInput(InputEvent.TypeChar('i'));
        ui.DrainEvents();
        Assert.Equal("hi", ui.Widget<EditBoxBehavior>(notes)!.Text);
        Assert.Equal("hi", ui.Label(notes)!.Value);
    }

    [Fact]
    public void EditboxWordEchoOnBoundary()
    {
        var ui = new CoreUi();
        var notes = ui.Editbox(NodeId.None, "Notes", "", false);
        ui.SetFocus(notes);
        foreach (var ch in "hey")
            ui.HandleInput(InputEvent.TypeChar(ch));
        ui.DrainEvents();
        ui.HandleInput(InputEvent.TypeChar(' '));
        Assert.Equal(new[] { "hey space" }, Spoken(ui.DrainEvents()));
    }

    [Fact]
    public void EditboxArrowNavigationSpeaksChars()
    {
        var ui = new CoreUi();
        var notes = ui.Editbox(NodeId.None, "Notes", "ab", false);
        ui.SetFocus(notes);
        ui.DrainEvents();

        // Cursor at 0; left is the top boundary.
        Assert.True(ui.HandleInput(Simple(InputKind.MoveLeft)));
        Assert.Equal(new[] { "Top, a" }, Spoken(ui.DrainEvents()));

        Assert.True(ui.HandleInput(Simple(InputKind.MoveRight)));
        Assert.Equal(new[] { "b" }, Spoken(ui.DrainEvents()));
    }

    [Fact]
    public void EditboxEnterSingleLineFallsThroughToPrimary()
    {
        var ui = new CoreUi();
        var notes = ui.Editbox(NodeId.None, "Notes", "", false);
        var ok = ui.Button(NodeId.None, "OK");
        ui.SetPrimary(ok);
        ui.SetFocus(notes);
        ui.DrainEvents();

        Assert.True(ui.HandleInput(Simple(InputKind.Activate)));
        Assert.True(ContainsActivated(ui.DrainEvents(), ok));
        Assert.Equal("", ui.Widget<EditBoxBehavior>(notes)!.Text);
    }

    [Fact]
    public void EditboxEnterMultilineInsertsNewline()
    {
        var ui = new CoreUi();
        var notes = ui.Editbox(NodeId.None, "Notes", "", true);
        ui.SetFocus(notes);
        ui.DrainEvents();

        Assert.True(ui.HandleInput(Simple(InputKind.Activate)));
        Assert.Equal(new[] { "new line" }, Spoken(ui.DrainEvents()));
        Assert.Equal("\n", ui.Widget<EditBoxBehavior>(notes)!.Text);
    }

    private sealed class MemClipboard : IClipboard
    {
        private string? _content;
        public string? Read() => _content;
        public void Write(string text) => _content = text;
    }

    [Fact]
    public void EditboxSelectAllAndCopy()
    {
        var ui = new CoreUi();
        ui.SetClipboard(new MemClipboard());
        var notes = ui.Editbox(NodeId.None, "Notes", "hello", false);
        ui.SetFocus(notes);
        ui.DrainEvents();

        ui.HandleInput(Simple(InputKind.SelectAll));
        Assert.Equal(new[] { "hello selected" }, Spoken(ui.DrainEvents()));

        ui.HandleInput(Simple(InputKind.Copy));
        Assert.Equal(new[] { "Copy" }, Spoken(ui.DrainEvents()));

        // Paste at the end doubles the text via the injected clipboard.
        ui.HandleInput(Simple(InputKind.MoveToDocEnd));
        ui.HandleInput(Simple(InputKind.Paste));
        ui.DrainEvents();
        Assert.Equal("hellohello", ui.Widget<EditBoxBehavior>(notes)!.Text);
    }

    [Fact]
    public void SetEditboxTextReannouncesWhenFocused()
    {
        var ui = new CoreUi();
        var notes = ui.Editbox(NodeId.None, "Notes", "old", false);
        ui.SetFocus(notes);
        ui.DrainEvents();

        ui.SetEditboxText(notes, "new text");
        Assert.Equal(new[] { "Notes edit new text" }, Spoken(ui.DrainEvents()));
        Assert.Equal("new text", ui.Widget<EditBoxBehavior>(notes)!.Text);
    }

    // ── Slider ──

    [Fact]
    public void SliderAdjustsAndAnnounces()
    {
        var ui = new CoreUi();
        var vol = ui.SliderWidget(NodeId.None, "Volume", new SliderBehavior(50, 0, 100, unit: "%"));
        ui.SetFocus(vol);
        Assert.Equal(new[] { "Volume slider 50%" }, Spoken(ui.DrainEvents()));

        Assert.True(ui.HandleInput(Simple(InputKind.MoveRight)));
        var events = ui.DrainEvents();
        Assert.Equal(new[] { "51%" }, Spoken(events));
        Assert.Contains(new CoreEvent.Changed(vol), events);

        // Large steps via Shift+arrow and PageDown.
        ui.HandleInput(Simple(InputKind.SelectRight));
        Assert.Equal(new[] { "61%" }, Spoken(ui.DrainEvents()));
        ui.HandleInput(Raw(KeyCombo.Plain(Key.PageDown)));
        Assert.Equal(new[] { "51%" }, Spoken(ui.DrainEvents()));

        // Home/End jump to the edges.
        ui.HandleInput(Simple(InputKind.MoveToLineEnd));
        Assert.Equal(new[] { "100%" }, Spoken(ui.DrainEvents()));
        Assert.Equal(100, ui.Widget<SliderBehavior>(vol)!.Value);

        // Clamped at max: consumed, re-announced, but no Changed event.
        ui.HandleInput(Simple(InputKind.MoveRight));
        var clamped = ui.DrainEvents();
        Assert.Equal(new[] { "100%" }, Spoken(clamped));
        Assert.DoesNotContain(clamped, ev => ev is CoreEvent.Changed);
    }

    [Fact]
    public void SetSliderValueSpeaksValueOnlyWhenFocused()
    {
        var ui = new CoreUi();
        var progress = ui.SliderWidget(NodeId.None, "Progress", new SliderBehavior(0, 0, 100, unit: "%"));
        ui.SetFocus(progress);
        ui.DrainEvents();

        // A programmatic move speaks like a user-driven one: the value,
        // not "Progress slider 30%".
        ui.SetSliderValue(progress, 30);
        Assert.Equal(new[] { "30%" }, Spoken(ui.DrainEvents()));
        Assert.Equal(30, ui.Widget<SliderBehavior>(progress)!.Value);

        // No change (clamped to the same value): silent.
        ui.SetSliderValue(progress, 200);
        ui.DrainEvents();
        ui.SetSliderValue(progress, 150);
        Assert.Empty(ui.DrainEvents());
        Assert.Equal(100, ui.Widget<SliderBehavior>(progress)!.Value);
    }

    [Fact]
    public void SetSliderValueSilentWhenUnfocused()
    {
        var ui = new CoreUi();
        var progress = ui.Slider(NodeId.None, "Progress", 0, 0, 100);
        var other = ui.Button(NodeId.None, "Other");
        ui.SetFocus(other);
        ui.DrainEvents();

        ui.SetSliderValue(progress, 30);
        Assert.Empty(ui.DrainEvents());
        Assert.Equal(30, ui.Widget<SliderBehavior>(progress)!.Value);
    }

    // ── TabControl ──

    [Fact]
    public void SetActiveTabSpeaksTabOnlyWhenFocused()
    {
        var ui = new CoreUi();
        var tabs = ui.TabControl(
            NodeId.None, "Views", new List<string> { "Files", "Playlist", "FX" }, 0);
        ui.SetFocus(tabs);
        ui.DrainEvents();

        ui.SetActiveTab(tabs, 1);
        Assert.Equal(new[] { "Playlist" }, Spoken(ui.DrainEvents()));
        Assert.Equal(1, ui.Widget<TabControlBehavior>(tabs)!.Active);
    }

    [Fact]
    public void TabControlCyclesWithWraparound()
    {
        var ui = new CoreUi();
        var tabs = ui.TabControl(
            NodeId.None, "Views", new List<string> { "Files", "Playlist", "FX" }, 0);
        ui.SetFocus(tabs);
        Assert.Equal(new[] { "Views tab control Files" }, Spoken(ui.DrainEvents()));

        ui.HandleInput(Simple(InputKind.MoveRight));
        Assert.Equal(new[] { "Playlist" }, Spoken(ui.DrainEvents()));
        ui.HandleInput(Simple(InputKind.MoveRight));
        Assert.Equal(new[] { "FX" }, Spoken(ui.DrainEvents()));
        ui.HandleInput(Simple(InputKind.MoveRight)); // wraps
        Assert.Equal(new[] { "Files" }, Spoken(ui.DrainEvents()));

        ui.HandleInput(Simple(InputKind.MoveLeft)); // wraps backward
        Assert.Equal(new[] { "FX" }, Spoken(ui.DrainEvents()));
        Assert.Equal(2, ui.Widget<TabControlBehavior>(tabs)!.Active);
        Assert.Equal("FX", ui.Label(tabs)!.Value);
    }

    // ── ShortcutField ──

    [Fact]
    public void ShortcutFieldCapturesAndClears()
    {
        var ui = new CoreUi();
        var field = ui.ShortcutField(NodeId.None, "Play shortcut");
        ui.SetFocus(field);
        Assert.Equal(new[] { "Play shortcut shortcut field blank" }, Spoken(ui.DrainEvents()));

        // A raw combo is captured verbatim.
        var ctrlS = KeyCombo.WithCtrl(Key.Char('s'));
        Assert.True(ui.HandleInput(Raw(ctrlS)));
        var events = ui.DrainEvents();
        Assert.Equal(new[] { "control s" }, Spoken(events));
        Assert.Contains(new CoreEvent.Changed(field), events);
        Assert.Equal(ctrlS, ui.Widget<ShortcutFieldBehavior>(field)!.Combo);

        // A semantically-mapped input is captured via its combo.
        Assert.True(ui.HandleInput(Simple(InputKind.Copy)));
        Assert.Equal(new[] { "control c" }, Spoken(ui.DrainEvents()));

        // Backspace clears.
        Assert.True(ui.HandleInput(Simple(InputKind.DeleteBackward)));
        Assert.Equal(new[] { "blank" }, Spoken(ui.DrainEvents()));
        Assert.Null(ui.Widget<ShortcutFieldBehavior>(field)!.Combo);

        // Tab still leaves the field.
        Assert.True(ui.HandleInput(Simple(InputKind.NavigateNext)));
    }

    [Fact]
    public void ShortcutFieldResistsFrameworkInterception()
    {
        var ui = new CoreUi();
        var group = ui.Group(NodeId.None, "Options");
        var field = ui.ShortcutField(group, "Shortcut");
        var other = ui.Button(NodeId.None, "Other");
        ui.AddShortcut(other, KeyCombo.WithAlt(Key.Char('o')), ShortcutAction.Jump);
        ui.SetFocus(field);
        ui.DrainEvents();

        // Alt+Up would be hierarchy navigation; the field captures it.
        ui.HandleInput(Simple(InputKind.TreeUp));
        Assert.Equal(field, ui.Focus);
        Assert.Equal(KeyCombo.WithAlt(Key.Up), ui.Widget<ShortcutFieldBehavior>(field)!.Combo);

        // Alt+O would be a mnemonic jump; the field captures it.
        ui.HandleInput(new InputEvent(InputKind.Shortcut, 'o', 0, Mods.None));
        Assert.Equal(field, ui.Focus);
        Assert.Equal(
            KeyCombo.WithAlt(Key.Char('o')),
            ui.Widget<ShortcutFieldBehavior>(field)!.Combo);

        // Ctrl+Tab arrives as RawKey and is captured (it is host-bindable).
        ui.HandleInput(Raw(KeyCombo.WithCtrl(Key.Tab)));
        Assert.Equal(KeyCombo.WithCtrl(Key.Tab), ui.Widget<ShortcutFieldBehavior>(field)!.Combo);

        // Escape still dismisses (unconsumed here — no cancel widget).
        Assert.False(ui.HandleInput(Simple(InputKind.Dismiss)));
        Assert.Equal(KeyCombo.WithCtrl(Key.Tab), ui.Widget<ShortcutFieldBehavior>(field)!.Combo);
    }

    // ── FilterListBox ──

    private static (CoreUi Ui, NodeId List) FilterUi()
    {
        var ui = new CoreUi();
        var list = ui.FilterListbox(
            NodeId.None, "Commands",
            new List<string> { "Save File", "Open Editor", "Quit" });
        ui.SetFocus(list);
        ui.DrainEvents();
        return (ui, list);
    }

    [Fact]
    public void FilterListboxFocusAnnouncementCarriesFilterState()
    {
        var ui = new CoreUi();
        var list = ui.FilterListbox(NodeId.None, "Commands", new List<string> { "Save File" });
        ui.SetFocus(list);
        Assert.Equal(new[] { "Commands list Save File no filter" }, Spoken(ui.DrainEvents()));
    }

    [Fact]
    public void FilterListboxTypingFiltersAndReports()
    {
        var ui = new CoreUi();
        var list = ui.FilterListbox(
            NodeId.None, "Commands",
            new List<string> { "Save File", "Settings", "Open Editor", "Quit" });
        ui.SetFocus(list);
        ui.DrainEvents();

        ui.HandleInput(InputEvent.TypeChar('s'));
        var events = ui.DrainEvents();
        Assert.Equal(new[] { "Save File 1 of 2" }, Spoken(events));
        Assert.Contains(new CoreEvent.Changed(list), events);
        Assert.Equal("filter s", ui.Label(list)!.StateText);

        ui.HandleInput(InputEvent.TypeChar('x'));
        Assert.Equal(new[] { "no results" }, Spoken(ui.DrainEvents()));
        Assert.Equal("empty", ui.Label(list)!.Value);

        // Backspace restores results.
        ui.HandleInput(Simple(InputKind.DeleteBackward));
        Assert.Equal(new[] { "Save File 1 of 2" }, Spoken(ui.DrainEvents()));
    }

    [Fact]
    public void FilterListboxArrowsNavigateFilteredSet()
    {
        var (ui, list) = FilterUi();
        ui.HandleInput(InputEvent.TypeChar('e'));
        ui.DrainEvents();

        ui.HandleInput(Simple(InputKind.MoveDown));
        var spoken = Spoken(ui.DrainEvents());
        Assert.Single(spoken);
        Assert.EndsWith("2 of 2", spoken[0]);

        // Bottom boundary repeats with prefix.
        ui.HandleInput(Simple(InputKind.MoveDown));
        Assert.StartsWith("bottom, ", Spoken(ui.DrainEvents())[0]);

        Assert.NotNull(ui.Widget<FilterListBoxBehavior>(list)!.SelectedItem());
    }

    [Fact]
    public void FilterListboxEnterFallsThroughToPrimary()
    {
        var (ui, list) = FilterUi();
        var open = ui.Button(NodeId.None, "Open");
        ui.SetPrimary(open);
        ui.HandleInput(InputEvent.TypeChar('q'));
        ui.DrainEvents();

        Assert.True(ui.HandleInput(Simple(InputKind.Activate)));
        Assert.True(ContainsActivated(ui.DrainEvents(), open));
        Assert.Equal("Quit", ui.Widget<FilterListBoxBehavior>(list)!.SelectedItem());
    }

    // ── Custom widgets ──

    [Fact]
    public void CustomWidgetFocusesAndPassesInputThrough()
    {
        var ui = new CoreUi();
        var before = ui.Button(NodeId.None, "Before");
        var arena = ui.Custom(NodeId.None, "Arena");
        var ok = ui.Button(NodeId.None, "OK");
        ui.SetPrimary(ok);
        ui.SetFocus(before);
        ui.DrainEvents();

        // In the tab ring, announced by bare name.
        ui.HandleInput(Simple(InputKind.NavigateNext));
        Assert.Equal(arena, ui.Focus);
        Assert.Equal(new[] { "Arena" }, Spoken(ui.DrainEvents()));

        // No built-in behavior: typing and arrows fall through to the
        // host; Enter still reaches the layer's primary.
        Assert.False(ui.HandleInput(InputEvent.TypeChar('q')));
        Assert.False(ui.HandleInput(Simple(InputKind.MoveUp)));
        Assert.True(ui.HandleInput(Simple(InputKind.Activate)));
        Assert.True(ContainsActivated(ui.DrainEvents(), ok));
    }

    // ── Dynamic state ──

    [Fact]
    public void HidingFocusedWidgetRecoversFocus()
    {
        var (ui, save, _, wrap) = DemoUi();
        ui.SetFocus(wrap);
        ui.DrainEvents();

        ui.SetHidden(wrap, true);
        Assert.Equal(save, ui.Focus);
        Assert.Equal(new[] { "Save button" }, Spoken(ui.DrainEvents()));
    }

    [Fact]
    public void DisablingFocusedWidgetRecoversFocus()
    {
        var (ui, save, _, wrap) = DemoUi();
        ui.SetFocus(wrap);
        ui.DrainEvents();

        ui.SetDisabled(wrap, true);
        Assert.Equal(save, ui.Focus);
    }

    [Fact]
    public void HidingGroupContainingFocusRecovers()
    {
        var (ui, save, options, wrap) = DemoUi();
        ui.SetFocus(wrap);
        ui.DrainEvents();

        ui.SetHidden(options, true);
        Assert.Equal(save, ui.Focus);
        // The hidden subtree is no longer tab-reachable.
        ui.DrainEvents();
        ui.HandleInput(Simple(InputKind.NavigateNext));
        Assert.Equal(save, ui.Focus);
    }

    [Fact]
    public void UnhideRestoresReachability()
    {
        var (ui, _, _, wrap) = DemoUi();
        ui.SetHidden(wrap, true);
        ui.SetHidden(wrap, false);
        ui.SetFocus(wrap);
        Assert.Equal(wrap, ui.Focus);
    }

    [Fact]
    public void RenameReannouncesFocusedNode()
    {
        var (ui, save, _, _) = DemoUi();
        ui.SetFocus(save);
        ui.DrainEvents();

        ui.SetNodeName(save, "Save All");
        Assert.Equal(new[] { "Save All button" }, Spoken(ui.DrainEvents()));

        // Unfocused mutation stays silent (description on the same node
        // while focused announces; move focus first).
        ui.SetNodeDescription(save, "writes every file");
        ui.DrainEvents();
    }

    // ── Tickers ──

    [Fact]
    public void TickerFiresOnClockAdvance()
    {
        var ui = new CoreUi();
        var ticker = ui.AddTicker(100);

        ui.SetNow(50);
        Assert.Empty(ui.DrainEvents());

        ui.SetNow(100);
        Assert.Equal(new CoreEvent[] { new CoreEvent.Tick(ticker) }, ui.DrainEvents());

        // A long gap fires once, not once per missed interval.
        ui.SetNow(950);
        Assert.Equal(new CoreEvent[] { new CoreEvent.Tick(ticker) }, ui.DrainEvents());
        // Next interval starts from the late check.
        ui.SetNow(1000);
        Assert.Empty(ui.DrainEvents());
        ui.SetNow(1050);
        Assert.Equal(new CoreEvent[] { new CoreEvent.Tick(ticker) }, ui.DrainEvents());
    }

    [Fact]
    public void RemovedTickerStopsFiring()
    {
        var ui = new CoreUi();
        var a = ui.AddTicker(10);
        var b = ui.AddTicker(10);
        ui.RemoveTicker(a);
        ui.SetNow(20);
        Assert.Equal(new CoreEvent[] { new CoreEvent.Tick(b) }, ui.DrainEvents());
    }

    [Fact]
    public void UnhandledInputIsUnconsumed()
    {
        var (ui, _, _, _) = DemoUi();
        ui.EnsureFocus();
        // A raw key nothing claims falls through to the host.
        Assert.False(ui.HandleInput(Raw(KeyCombo.WithCtrl(Key.Char('s')))));
    }
}
