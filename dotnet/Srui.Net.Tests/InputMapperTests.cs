using System.Runtime.InteropServices;
using Srui;
using Srui.Core;
using Xunit;

namespace Srui.Net.Tests;

/// <summary>The physical-provenance contract of the SDL input mapper:
/// every logical input carries the combo that produced it, including
/// typed characters whose keydown was suppressed in favor of the
/// following TextInput event.</summary>
public class InputMapperTests
{
    private static Sdl3.Event KeyDown(uint key, ushort mod) => new()
    {
        Type = Sdl3.EventKeyDown,
        Key = key,
        Mod = mod,
    };

    private static InputEvent? Text(InputMapper mapper, string s)
    {
        var ptr = Marshal.StringToCoTaskMemUTF8(s);
        try
        {
            var ev = new Sdl3.Event { Type = Sdl3.EventTextInput, TextPtr = ptr };
            return mapper.Map(ev);
        }
        finally
        {
            Marshal.FreeCoTaskMem(ptr);
        }
    }

    [Fact]
    public void ShiftedTypingCarriesItsPhysicalCombo()
    {
        var mapper = new InputMapper();
        // The keydown is suppressed (the rune arrives as TextInput)...
        Assert.Null(mapper.Map(KeyDown('q', Sdl3.KmodShift)));
        // ...and the TypeChar carries both the rune and the real combo.
        var typed = Text(mapper, "Q");
        Assert.Equal(InputKind.TypeChar, typed?.Kind);
        Assert.Equal('Q', (char?)typed?.Ch);
        Assert.Equal(KeyCombo.WithShift(Key.Char('q')), KeyCombo.FromInput(typed!.Value));
    }

    [Fact]
    public void PlainTypingCarriesItsPhysicalCombo()
    {
        var mapper = new InputMapper();
        Assert.Null(mapper.Map(KeyDown('q', 0)));
        var typed = Text(mapper, "q");
        Assert.Equal(KeyCombo.Plain(Key.Char('q')), KeyCombo.FromInput(typed!.Value));
    }

    [Fact]
    public void ShiftBackspaceKeepsItsCombo()
    {
        // shift+backspace and ctrl+backspace both map to word-delete;
        // the event remembers which one was pressed.
        var mapper = new InputMapper();
        var ev = mapper.Map(KeyDown(Sdl3.KeyBackspace, Sdl3.KmodShift));
        Assert.Equal(InputKind.DeleteWordBackward, ev?.Kind);
        Assert.Equal(KeyCombo.WithShift(Key.Backspace), KeyCombo.FromInput(ev!.Value));

        var ctrl = mapper.Map(KeyDown(Sdl3.KeyBackspace, Sdl3.KmodCtrl));
        Assert.Equal(InputKind.DeleteWordBackward, ctrl?.Kind);
        Assert.Equal(KeyCombo.WithCtrl(Key.Backspace), KeyCombo.FromInput(ctrl!.Value));
    }

    [Fact]
    public void SemanticInputsCarryTheirCombos()
    {
        var mapper = new InputMapper();
        var enter = mapper.Map(KeyDown(Sdl3.KeyReturn, 0));
        Assert.Equal(InputKind.Activate, enter?.Kind);
        Assert.Equal(KeyCombo.Plain(Key.Enter), KeyCombo.FromInput(enter!.Value));

        var selectLeft = mapper.Map(KeyDown(Sdl3.KeyLeft, Sdl3.KmodShift));
        Assert.Equal(InputKind.SelectLeft, selectLeft?.Kind);
        Assert.Equal(KeyCombo.WithShift(Key.Left), KeyCombo.FromInput(selectLeft!.Value));
    }

    [Fact]
    public void PendingTypedKeyIsOneShot()
    {
        var mapper = new InputMapper();
        mapper.Map(KeyDown('q', Sdl3.KmodShift));
        _ = Text(mapper, "Q");
        // A second TextInput without a fresh keydown (IME commit, dead-key
        // compose) has no physical provenance.
        var stray = Text(mapper, "é");
        Assert.Equal(0u, stray?.Key);
    }
}
