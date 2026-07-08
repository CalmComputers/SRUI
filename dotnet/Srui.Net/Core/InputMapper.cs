using System.Runtime.InteropServices;
using System.Text;

namespace Srui.Core;

/// <summary>Physical → logical input mapping: SDL3 keyboard events plus
/// modifiers to <see cref="InputEvent"/>. Combos with no semantic meaning
/// surface as RawKey for the host's own bindings — there are no
/// application shortcuts at this layer.
///
/// Stateful: tracks whether Ctrl/Alt are held so text-input events can be
/// suppressed when modifiers are active (SDL3 sends TextInput for
/// Ctrl+Space on some platforms, which would otherwise produce a phantom
/// TypeChar). Also detects clean Alt taps (press and release with nothing
/// in between), which hosts commonly bind to a menu or palette.</summary>
internal sealed class InputMapper
{
    private bool _modifiersHeld;
    /// <summary>True while Alt is physically held and no other key has
    /// been pressed.</summary>
    private bool _altClean;
    /// <summary>Alt tap detected — deferred until the batch is drained,
    /// so a FocusLost in the same batch can cancel it.</summary>
    private bool _altTapPending;

    private static bool IsModifierKey(uint keycode) => keycode
        is Sdl3.KeyLCtrl or Sdl3.KeyRCtrl
        or Sdl3.KeyLAlt or Sdl3.KeyRAlt
        or Sdl3.KeyLShift or Sdl3.KeyRShift;

    /// <summary>Map an SDL3 event to a logical input, if applicable.</summary>
    public InputEvent? Map(in Sdl3.Event ev)
    {
        switch (ev.Type)
        {
            case Sdl3.EventKeyDown:
            {
                var ctrl = (ev.Mod & Sdl3.KmodCtrl) != 0;
                var alt = (ev.Mod & Sdl3.KmodAlt) != 0;
                _modifiersHeld = ctrl || alt;

                // Track clean Alt tap: Alt down with nothing else → clean.
                // Any non-modifier key while Alt is held → dirty.
                if (ev.Key is Sdl3.KeyLAlt or Sdl3.KeyRAlt && !ctrl)
                    _altClean = true;
                else if (!IsModifierKey(ev.Key))
                    _altClean = false;

                return MapKeyDown(ev.Key, ev.Mod);
            }

            case Sdl3.EventKeyUp:
            {
                var ctrl = (ev.Mod & Sdl3.KmodCtrl) != 0;
                var alt = (ev.Mod & Sdl3.KmodAlt) != 0;
                _modifiersHeld = ctrl || alt;

                // Alt released and nothing else was pressed → defer the
                // tap. Not emitted immediately because FocusLost (from
                // Alt+Tab) may arrive later in the same batch.
                if (ev.Key is Sdl3.KeyLAlt or Sdl3.KeyRAlt && !alt && _altClean)
                {
                    _altClean = false;
                    _altTapPending = true;
                }
                return null;
            }

            case Sdl3.EventWindowFocusLost:
                _altClean = false;
                _altTapPending = false;
                return null;

            case Sdl3.EventWindowFocusGained:
                _altClean = false;
                _altTapPending = false;
                return InputEvent.Simple(InputKind.SpeakFocus);

            case Sdl3.EventTextInput:
            {
                if (_modifiersHeld || ev.TextPtr == IntPtr.Zero)
                    return null;
                var text = Marshal.PtrToStringUTF8(ev.TextPtr);
                if (string.IsNullOrEmpty(text))
                    return null;
                // Exactly one scalar, not a control character.
                var rune = Rune.GetRuneAt(text, 0);
                if (rune.Utf16SequenceLength == text.Length && !Rune.IsControl(rune))
                    return new InputEvent(InputKind.TypeChar, (uint)rune.Value, 0, Mods.None);
                return null;
            }

            default:
                return null;
        }
    }

    /// <summary>Consume the deferred Alt tap, if any. Call after draining
    /// all events in the pump cycle so FocusLost can cancel it.</summary>
    public bool TakeAltTap()
    {
        var pending = _altTapPending;
        _altTapPending = false;
        return pending;
    }

    private static InputEvent? MapKeyDown(uint keycode, ushort keymod)
    {
        var ctrl = (keymod & Sdl3.KmodCtrl) != 0;
        var alt = (keymod & Sdl3.KmodAlt) != 0;
        var shift = (keymod & Sdl3.KmodShift) != 0;

        // Alt+arrow → tree navigation; Alt+letter → widget mnemonic.
        if (alt && !ctrl && !shift)
        {
            switch (keycode)
            {
                case Sdl3.KeyUp: return InputEvent.Simple(InputKind.TreeUp);
                case Sdl3.KeyDown: return InputEvent.Simple(InputKind.TreeDown);
                case Sdl3.KeyLeft: return InputEvent.Simple(InputKind.TreeLeft);
                case Sdl3.KeyRight: return InputEvent.Simple(InputKind.TreeRight);
            }
            if (keycode is >= 'a' and <= 'z')
                return new InputEvent(InputKind.Shortcut, keycode, 0, Mods.None);
        }

        // Ctrl+key combos.
        if (ctrl && !alt)
        {
            switch (keycode)
            {
                case 'c' when !shift: return InputEvent.Simple(InputKind.Copy);
                case 'x' when !shift: return InputEvent.Simple(InputKind.Cut);
                case 'v' when !shift: return InputEvent.Simple(InputKind.Paste);
                case 'a' when !shift: return InputEvent.Simple(InputKind.SelectAll);

                case Sdl3.KeyLeft when shift: return InputEvent.Simple(InputKind.SelectWordLeft);
                case Sdl3.KeyRight when shift: return InputEvent.Simple(InputKind.SelectWordRight);
                case Sdl3.KeyLeft: return InputEvent.Simple(InputKind.MoveWordLeft);
                case Sdl3.KeyRight: return InputEvent.Simple(InputKind.MoveWordRight);

                case Sdl3.KeyHome when shift: return InputEvent.Simple(InputKind.SelectToDocStart);
                case Sdl3.KeyEnd when shift: return InputEvent.Simple(InputKind.SelectToDocEnd);
                case Sdl3.KeyHome: return InputEvent.Simple(InputKind.MoveToDocStart);
                case Sdl3.KeyEnd: return InputEvent.Simple(InputKind.MoveToDocEnd);

                case Sdl3.KeyBackspace: return InputEvent.Simple(InputKind.DeleteWordBackward);
                case Sdl3.KeyDelete: return InputEvent.Simple(InputKind.DeleteWordForward);
            }
        }

        // Shift+movement → selection, Shift+Backspace/Delete → word delete.
        if (shift && !ctrl && !alt)
        {
            switch (keycode)
            {
                case Sdl3.KeyLeft: return InputEvent.Simple(InputKind.SelectLeft);
                case Sdl3.KeyRight: return InputEvent.Simple(InputKind.SelectRight);
                case Sdl3.KeyUp: return InputEvent.Simple(InputKind.SelectLineUp);
                case Sdl3.KeyDown: return InputEvent.Simple(InputKind.SelectLineDown);
                case Sdl3.KeyHome: return InputEvent.Simple(InputKind.SelectToLineStart);
                case Sdl3.KeyEnd: return InputEvent.Simple(InputKind.SelectToLineEnd);

                case Sdl3.KeyBackspace: return InputEvent.Simple(InputKind.DeleteWordBackward);
                case Sdl3.KeyDelete: return InputEvent.Simple(InputKind.DeleteWordForward);
            }
        }

        // Plain keys.
        if (!alt && !ctrl)
        {
            switch (keycode)
            {
                case Sdl3.KeyTab when shift: return InputEvent.Simple(InputKind.NavigatePrev);
                case Sdl3.KeyTab: return InputEvent.Simple(InputKind.NavigateNext);

                case Sdl3.KeyEscape when !shift: return InputEvent.Simple(InputKind.Dismiss);
                case Sdl3.KeyReturn or Sdl3.KeyKpEnter when shift:
                    return InputEvent.Simple(InputKind.SecondaryActivate);
                case Sdl3.KeyReturn or Sdl3.KeyKpEnter:
                    return InputEvent.Simple(InputKind.Activate);

                case Sdl3.KeyUp when !shift: return InputEvent.Simple(InputKind.MoveUp);
                case Sdl3.KeyDown when !shift: return InputEvent.Simple(InputKind.MoveDown);
                case Sdl3.KeyLeft when !shift: return InputEvent.Simple(InputKind.MoveLeft);
                case Sdl3.KeyRight when !shift: return InputEvent.Simple(InputKind.MoveRight);

                case Sdl3.KeyHome when !shift: return InputEvent.Simple(InputKind.MoveToLineStart);
                case Sdl3.KeyEnd when !shift: return InputEvent.Simple(InputKind.MoveToLineEnd);

                case Sdl3.KeyBackspace: return InputEvent.Simple(InputKind.DeleteBackward);
                case Sdl3.KeyDelete: return InputEvent.Simple(InputKind.DeleteForward);
            }
        }

        // No semantic mapping — emit RawKey for the host's shortcut
        // matching. Skip keys that will also arrive as TextInput →
        // TypeChar, to avoid double-firing shortcuts: unmodified printable
        // keys (letters, digits, space, punctuation).
        if (KeycodeToKey(keycode) is not Key key)
            return null;
        if (!ctrl && !alt && (key.IsChar(out _) || key == Key.Space))
            return null;
        var mods = (ctrl ? Mods.Ctrl : Mods.None)
            | (alt ? Mods.Alt : Mods.None)
            | (shift ? Mods.Shift : Mods.None);
        return InputEvent.RawKey(key.Code, mods);
    }

    /// <summary>The physical combo for an SDL key event, in the flat
    /// (key, mods) encoding, when the key has one. Bare modifier presses
    /// have none.</summary>
    public static (uint Key, Mods Mods)? PhysicalCombo(uint keycode, ushort keymod)
    {
        if (KeycodeToKey(keycode) is not Key key)
            return null;
        var mods = ((keymod & Sdl3.KmodCtrl) != 0 ? Mods.Ctrl : Mods.None)
            | ((keymod & Sdl3.KmodAlt) != 0 ? Mods.Alt : Mods.None)
            | ((keymod & Sdl3.KmodShift) != 0 ? Mods.Shift : Mods.None);
        return (key.Code, mods);
    }

    private static Key? KeycodeToKey(uint keycode)
    {
        // Letters, digits, and the punctuation row are their ASCII
        // codepoints in SDL.
        if (keycode is >= 'a' and <= 'z' or >= '0' and <= '9')
            return Key.Char((char)keycode);
        switch (keycode)
        {
            case '[' or ']' or ';' or '\'' or ',' or '.' or '/' or '\\' or '`' or '-' or '=':
                return Key.Char((char)keycode);
        }
        if (keycode is >= Sdl3.KeyF1 and <= Sdl3.KeyF12)
            return Key.F((byte)(keycode - Sdl3.KeyF1 + 1));
        return keycode switch
        {
            Sdl3.KeyUp => Key.Up,
            Sdl3.KeyDown => Key.Down,
            Sdl3.KeyLeft => Key.Left,
            Sdl3.KeyRight => Key.Right,
            Sdl3.KeyHome => Key.Home,
            Sdl3.KeyEnd => Key.End,
            Sdl3.KeyPageUp => Key.PageUp,
            Sdl3.KeyPageDown => Key.PageDown,
            Sdl3.KeyReturn or Sdl3.KeyKpEnter => Key.Enter,
            Sdl3.KeyEscape => Key.Escape,
            Sdl3.KeyTab => Key.Tab,
            Sdl3.KeySpace => Key.Space,
            Sdl3.KeyBackspace => Key.Backspace,
            Sdl3.KeyDelete => Key.Delete,
            Sdl3.KeyMediaPlayPause or Sdl3.KeyMediaPlay or Sdl3.KeyMediaPause => Key.MediaPlayPause,
            Sdl3.KeyMediaNextTrack => Key.MediaNextTrack,
            Sdl3.KeyMediaPreviousTrack => Key.MediaPreviousTrack,
            Sdl3.KeyMediaStop => Key.MediaStop,
            _ => null,
        };
    }
}
