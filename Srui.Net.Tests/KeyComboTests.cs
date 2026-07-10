using Srui;
using Srui.Core;
using Xunit;

namespace Srui.Net.Tests;

public class KeyComboTests
{
    [Fact]
    public void DisplayPlainKey()
    {
        Assert.Equal("enter", KeyCombo.Plain(Key.Enter).DisplayName());
        Assert.Equal("space", KeyCombo.Plain(Key.Space).DisplayName());
    }

    [Fact]
    public void DisplayCtrlKey()
    {
        Assert.Equal("control s", KeyCombo.WithCtrl(Key.Char('s')).DisplayName());
        Assert.Equal("control home", KeyCombo.WithCtrl(Key.Home).DisplayName());
    }

    [Fact]
    public void DisplayMultiModifier()
    {
        var combo = new KeyCombo(Key.Char('p'), true, false, true);
        Assert.Equal("control shift p", combo.DisplayName());
    }

    [Fact]
    public void DisplayAltF4()
    {
        var combo = new KeyCombo(Key.F(4), false, true, false);
        Assert.Equal("alt f4", combo.DisplayName());
    }

    [Fact]
    public void FromInputCopy()
    {
        var combo = KeyCombo.FromInput(InputEvent.Simple(InputKind.Copy));
        Assert.Equal(KeyCombo.WithCtrl(Key.Char('c')), combo);
    }

    [Fact]
    public void FromInputTypeChar()
    {
        var combo = KeyCombo.FromInput(InputEvent.TypeChar('a'));
        Assert.Equal(KeyCombo.Plain(Key.Char('a')), combo);
    }

    [Fact]
    public void FromInputSpace()
    {
        var combo = KeyCombo.FromInput(InputEvent.TypeChar(' '));
        Assert.Equal(KeyCombo.Plain(Key.Space), combo);
    }

    [Fact]
    public void FromInputDismiss()
    {
        var combo = KeyCombo.FromInput(InputEvent.Simple(InputKind.Dismiss));
        Assert.Equal(KeyCombo.Plain(Key.Escape), combo);
    }

    [Fact]
    public void FromInputRawKeyPassthrough()
    {
        var raw = InputEvent.RawKey(Key.Char('s').Code, Mods.Ctrl);
        var combo = KeyCombo.FromInput(raw);
        Assert.Equal(KeyCombo.WithCtrl(Key.Char('s')), combo);
    }

    [Fact]
    public void FromInputSpeakFocusReturnsNull()
    {
        Assert.Null(KeyCombo.FromInput(InputEvent.Simple(InputKind.SpeakFocus)));
    }

    [Fact]
    public void MatchesInputPositive()
    {
        var ctrlS = KeyCombo.WithCtrl(Key.Char('s'));
        var raw = InputEvent.RawKey(Key.Char('s').Code, Mods.Ctrl);
        Assert.True(ctrlS.MatchesInput(raw));
    }

    [Fact]
    public void MatchesInputNegative()
    {
        var ctrlS = KeyCombo.WithCtrl(Key.Char('s'));
        Assert.False(ctrlS.MatchesInput(InputEvent.Simple(InputKind.Copy)));
    }

    [Fact]
    public void MatchesInputViaSemantic()
    {
        var ctrlC = KeyCombo.WithCtrl(Key.Char('c'));
        Assert.True(ctrlC.MatchesInput(InputEvent.Simple(InputKind.Copy)));
    }

    [Fact]
    public void ShortcutMapsToAltChar()
    {
        var combo = KeyCombo.FromInput(new InputEvent(InputKind.Shortcut, 's', 0, Mods.None));
        Assert.Equal(new KeyCombo(Key.Char('s'), false, true, false), combo);
    }

    // ── Config string roundtrip ──

    [Fact]
    public void ConfigStringPlainKey()
    {
        Assert.Equal("enter", KeyCombo.Plain(Key.Enter).ToConfigString());
    }

    [Fact]
    public void ConfigStringCtrlS()
    {
        Assert.Equal("ctrl+s", KeyCombo.WithCtrl(Key.Char('s')).ToConfigString());
    }

    [Fact]
    public void ConfigStringCtrlShiftP()
    {
        var combo = new KeyCombo(Key.Char('p'), true, false, true);
        Assert.Equal("ctrl+shift+p", combo.ToConfigString());
    }

    [Fact]
    public void ConfigStringAltF4()
    {
        var combo = new KeyCombo(Key.F(4), false, true, false);
        Assert.Equal("alt+f4", combo.ToConfigString());
    }

    [Fact]
    public void ConfigStringRoundtrip()
    {
        var combos = new[]
        {
            KeyCombo.Plain(Key.Enter),
            KeyCombo.WithCtrl(Key.Char('s')),
            KeyCombo.WithAlt(Key.F(12)),
            KeyCombo.CtrlShift(Key.Char('p')),
            new KeyCombo(Key.Delete, true, true, true),
            KeyCombo.Plain(Key.PageUp),
            KeyCombo.Plain(Key.MediaPlayPause),
        };
        foreach (var combo in combos)
        {
            var s = combo.ToConfigString();
            Assert.True(KeyCombo.TryParseConfig(s, out var parsed), $"parse failed for {s}");
            Assert.Equal(combo, parsed);
        }
    }

    [Fact]
    public void ParseAliases()
    {
        Assert.True(KeyCombo.TryParseConfig("control+esc", out var esc));
        Assert.Equal(KeyCombo.WithCtrl(Key.Escape), esc);
        Assert.True(KeyCombo.TryParseConfig("ctrl+del", out var del));
        Assert.Equal(KeyCombo.WithCtrl(Key.Delete), del);
        Assert.True(KeyCombo.TryParseConfig("ctrl+pgup", out var pgup));
        Assert.Equal(KeyCombo.WithCtrl(Key.PageUp), pgup);
    }

    [Fact]
    public void ParseRejectsEmpty()
    {
        Assert.False(KeyCombo.TryParseConfig("", out _));
    }

    [Fact]
    public void ParseRejectsDoubleKey()
    {
        Assert.False(KeyCombo.TryParseConfig("a+b", out _));
    }
}
