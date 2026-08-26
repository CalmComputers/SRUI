using System.Runtime.InteropServices;
using Srui;
using Srui.Core;
using Xunit;

namespace Srui.Tests;

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
    public void ShiftedEditingKeysFollowWindowsConventions()
    {
        // Shift+Backspace is plain backspace, Shift+Delete is cut,
        // Ctrl+Backspace is word-delete — each carrying its own combo.
        var mapper = new InputMapper();
        var shiftBackspace = mapper.Map(KeyDown(Sdl3.KeyBackspace, Sdl3.KmodShift));
        Assert.Equal(InputKind.DeleteBackward, shiftBackspace?.Kind);
        Assert.Equal(KeyCombo.WithShift(Key.Backspace), KeyCombo.FromInput(shiftBackspace!.Value));

        var shiftDelete = mapper.Map(KeyDown(Sdl3.KeyDelete, Sdl3.KmodShift));
        Assert.Equal(InputKind.Cut, shiftDelete?.Kind);
        Assert.Equal(KeyCombo.WithShift(Key.Delete), KeyCombo.FromInput(shiftDelete!.Value));

        var ctrl = mapper.Map(KeyDown(Sdl3.KeyBackspace, Sdl3.KmodCtrl));
        Assert.Equal(InputKind.DeleteWordBackward, ctrl?.Kind);
        Assert.Equal(KeyCombo.WithCtrl(Key.Backspace), KeyCombo.FromInput(ctrl!.Value));
    }

    [Fact]
    public void ModifierKeysAreInThePhysicalStreamOnly()
    {
        // A bare Shift press is a physical key a game may bind (the
        // host reports it through the key stream)...
        var physical = InputMapper.PhysicalCombo(Sdl3.KeyLShift, Sdl3.KmodShift);
        Assert.Equal(Key.Shift.Code, physical?.Key);
        Assert.Equal(Key.Shift.Code, InputMapper.PhysicalCombo(Sdl3.KeyRShift, 0)?.Key);
        Assert.Equal(Key.Ctrl.Code, InputMapper.PhysicalCombo(Sdl3.KeyLCtrl, 0)?.Key);
        Assert.Equal(Key.Alt.Code, InputMapper.PhysicalCombo(Sdl3.KeyRAlt, 0)?.Key);

        // ...and never a logical input: the keydown maps to nothing,
        // not to a RawKey the host's bindings would have to ignore.
        var mapper = new InputMapper();
        Assert.Null(mapper.Map(KeyDown(Sdl3.KeyLShift, Sdl3.KmodShift)));
        Assert.Null(mapper.Map(KeyDown(Sdl3.KeyLCtrl, Sdl3.KmodCtrl)));
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

/// <summary>The Windows key: never semantic, always a host binding, and
/// a text suppressor like Ctrl and Alt.</summary>
public class WindowsKeyMappingTests
{
    private static Sdl3.Event KeyDown(uint key, ushort mod) => new()
    {
        Type = Sdl3.EventKeyDown,
        Key = key,
        Mod = mod,
    };

    [Fact]
    public void WinCombosAreRawKeysCarryingTheModifier()
    {
        var mapper = new InputMapper();
        var left = mapper.Map(KeyDown(Sdl3.KeyLeft, Sdl3.KmodLGui));
        Assert.Equal(InputKind.RawKey, left?.Kind);
        Assert.Equal(KeyCombo.WithWin(Key.Left), KeyCombo.FromInput(left!.Value));

        var mnemonic = mapper.Map(KeyDown('s', (ushort)(Sdl3.KmodRGui | Sdl3.KmodLAlt)));
        Assert.Equal(InputKind.RawKey, mnemonic?.Kind);
        Assert.Equal(KeyCombo.WinAlt(Key.Char('s')), KeyCombo.FromInput(mnemonic!.Value));
    }

    [Fact]
    public void WinPlusPrintableNeverTypes()
    {
        var mapper = new InputMapper();
        var down = mapper.Map(KeyDown('k', Sdl3.KmodLGui));
        Assert.Equal(InputKind.RawKey, down?.Kind);
        // Some platforms still send TextInput for the key; it is dropped.
        var ptr = System.Runtime.InteropServices.Marshal.StringToCoTaskMemUTF8("k");
        try
        {
            Assert.Null(mapper.Map(new Sdl3.Event { Type = Sdl3.EventTextInput, TextPtr = ptr }));
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeCoTaskMem(ptr);
        }
    }

    [Fact]
    public void TheWindowsKeyItselfIsPhysicalOnly()
    {
        var mapper = new InputMapper();
        Assert.Null(mapper.Map(KeyDown(Sdl3.KeyLGui, Sdl3.KmodLGui)));
        Assert.Equal((Key.Win.Code, Mods.Win), InputMapper.PhysicalCombo(Sdl3.KeyLGui, Sdl3.KmodLGui));
    }
}
