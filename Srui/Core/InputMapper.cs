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
    /// <summary>The physical combo of a printable keydown whose logical
    /// input arrives as the next TextInput event (SDL sends KEY_DOWN
    /// before TEXT_INPUT for the generating key). Lets the TypeChar carry
    /// its physical provenance — shift+q is representable even though the
    /// typed rune is just 'Q'. One-shot: consumed by the TextInput,
    /// cleared by any other keydown or a focus transition.</summary>
    private (uint Key, Mods Mods)? _pendingTypedKey;
    /// <summary>True while Alt is physically held and no other key has
    /// been pressed.</summary>
    private bool _altClean;
    /// <summary>Alt tap detected — deferred until the batch is drained,
    /// so a FocusLost in the same batch can cancel it.</summary>
    private bool _altTapPending;

    private static bool IsModifierKey(uint keycode) => keycode
        is Sdl3.KeyLCtrl or Sdl3.KeyRCtrl
        or Sdl3.KeyLAlt or Sdl3.KeyRAlt
        or Sdl3.KeyLShift or Sdl3.KeyRShift
        or Sdl3.KeyLGui or Sdl3.KeyRGui;

    /// <summary>Map an SDL3 event to a logical input, if applicable.</summary>
    public InputEvent? Map(in Sdl3.Event ev)
    {
        switch (ev.Type)
        {
            case Sdl3.EventKeyDown:
            {
                var ctrl = (ev.Mod & Sdl3.KmodCtrl) != 0;
                var alt = (ev.Mod & Sdl3.KmodAlt) != 0;
                var win = (ev.Mod & Sdl3.KmodGui) != 0;
                _modifiersHeld = ctrl || alt || win;

                // Track clean Alt tap: Alt down with nothing else → clean.
                // Any non-modifier key while Alt is held → dirty, and so
                // is the Windows key: Win+Alt is the system-hotkey range,
                // and the OS eats the key that would otherwise dirty it.
                if (ev.Key is Sdl3.KeyLAlt or Sdl3.KeyRAlt && !ctrl && !win)
                    _altClean = true;
                else if (!IsModifierKey(ev.Key) || ev.Key is Sdl3.KeyLGui or Sdl3.KeyRGui)
                    _altClean = false;

                // A modifier on its own is a physical key (the host's
                // key stream reports its press) but never a logical
                // input — the host would otherwise see it as a RawKey.
                var mapped = IsModifierKey(ev.Key) ? null : MapKeyDown(ev.Key, ev.Mod);
                // A suppressed printable arrives next as TextInput;
                // remember the physical key so the TypeChar carries it.
                if (!IsModifierKey(ev.Key))
                {
                    _pendingTypedKey = null;
                    if (mapped is null && !ctrl && !alt && !win
                        && PhysicalCombo(ev.Key, ev.Mod) is (var key, var mods)
                        && (new Key(key).IsChar(out _) || new Key(key) == Key.Space))
                        _pendingTypedKey = (key, mods);
                }
                return mapped;
            }

            case Sdl3.EventKeyUp:
            {
                var ctrl = (ev.Mod & Sdl3.KmodCtrl) != 0;
                var alt = (ev.Mod & Sdl3.KmodAlt) != 0;
                _modifiersHeld = ctrl || alt || (ev.Mod & Sdl3.KmodGui) != 0;

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
                _pendingTypedKey = null;
                return null;

            case Sdl3.EventWindowFocusGained:
                _altClean = false;
                _altTapPending = false;
                _pendingTypedKey = null;
                return InputEvent.Simple(InputKind.SpeakFocus);

            case Sdl3.EventTextInput:
            {
                var pending = _pendingTypedKey;
                _pendingTypedKey = null;
                if (_modifiersHeld || ev.TextPtr == IntPtr.Zero)
                    return null;
                var text = Marshal.PtrToStringUTF8(ev.TextPtr);
                if (string.IsNullOrEmpty(text))
                    return null;
                // Exactly one scalar, not a control character.
                var rune = Rune.GetRuneAt(text, 0);
                if (rune.Utf16SequenceLength == text.Length && !Rune.IsControl(rune))
                    return new InputEvent(
                        InputKind.TypeChar, (uint)rune.Value,
                        pending?.Key ?? 0, pending?.Mods ?? Mods.None);
                return null;
            }

            default:
                return null;
        }
    }

    /// <summary>Consume the deferred Alt tap, if any. Call after draining
    /// all events in the pump cycle so FocusLost can cancel it.</summary>
    /// <summary>A system-wide hotkey fired: whatever Alt is doing, it
    /// was part of that combo, not a tap.</summary>
    public void CancelAltTap()
    {
        _altClean = false;
        _altTapPending = false;
    }

    public bool TakeAltTap()
    {
        var pending = _altTapPending;
        _altTapPending = false;
        return pending;
    }

    /// <summary>Map a keydown to its logical input: SDL keycode to
    /// physical combo, then the shared combo mapping.</summary>
    private static InputEvent? MapKeyDown(uint keycode, ushort keymod)
    {
        if (PhysicalCombo(keycode, keymod) is not (var key, var mods))
            return null;
        return MapCombo(KeyCombo.FromFlat(key, mods));
    }

    /// <summary>Map a physical combo to the logical input its keydown
    /// produces, stamped with the combo as physical provenance so
    /// shortcut matching and combo capture never reverse-map. The pure
    /// half of keydown mapping, shared by the SDL path and synthetic
    /// hosts (the test harness). Null when the keydown yields no logical
    /// input directly: unmodified printables arrive as the following
    /// TextInput → TypeChar instead.</summary>
    public static InputEvent? MapCombo(KeyCombo combo)
    {
        if (MapComboInner(combo) is not InputEvent mapped)
            return null;
        if (mapped.Key != 0)
            return mapped;
        var (key, mods) = combo.ToFlat();
        return mapped with { Key = key, Mods = mods };
    }

    private static InputEvent? MapComboInner(KeyCombo combo)
    {
        var (key, ctrl, alt, shift) = (combo.Key, combo.Ctrl, combo.Alt, combo.Shift);

        // The Windows key has no semantic mapping of its own and turns
        // any combo into a host binding: win+left is not "move left".
        if (combo.Win)
        {
            var (winKey, winMods) = combo.ToFlat();
            return InputEvent.RawKey(winKey, winMods);
        }

        // Alt+arrow → tree navigation; Alt+letter → widget mnemonic.
        if (alt && !ctrl && !shift)
        {
            if (key == Key.Up) return InputEvent.Simple(InputKind.TreeUp);
            if (key == Key.Down) return InputEvent.Simple(InputKind.TreeDown);
            if (key == Key.Left) return InputEvent.Simple(InputKind.TreeLeft);
            if (key == Key.Right) return InputEvent.Simple(InputKind.TreeRight);
            if (key.IsChar(out var mnemonic) && char.IsAsciiLetter(mnemonic))
                return new InputEvent(
                    InputKind.Shortcut, char.ToLowerInvariant(mnemonic), 0, Mods.None);
        }

        // Ctrl+key combos.
        if (ctrl && !alt)
        {
            if (!shift && key == Key.Char('c')) return InputEvent.Simple(InputKind.Copy);
            if (!shift && key == Key.Char('x')) return InputEvent.Simple(InputKind.Cut);
            if (!shift && key == Key.Char('v')) return InputEvent.Simple(InputKind.Paste);
            if (!shift && key == Key.Char('a')) return InputEvent.Simple(InputKind.SelectAll);
            if (!shift && key == Key.Char('z')) return InputEvent.Simple(InputKind.Undo);
            if (!shift && key == Key.Char('y')) return InputEvent.Simple(InputKind.Redo);
            if (shift && key == Key.Char('z')) return InputEvent.Simple(InputKind.Redo);

            if (key == Key.Left)
                return InputEvent.Simple(shift ? InputKind.SelectWordLeft : InputKind.MoveWordLeft);
            if (key == Key.Right)
                return InputEvent.Simple(shift ? InputKind.SelectWordRight : InputKind.MoveWordRight);
            if (key == Key.Home)
                return InputEvent.Simple(shift ? InputKind.SelectToDocStart : InputKind.MoveToDocStart);
            if (key == Key.End)
                return InputEvent.Simple(shift ? InputKind.SelectToDocEnd : InputKind.MoveToDocEnd);

            if (key == Key.Backspace) return InputEvent.Simple(InputKind.DeleteWordBackward);
            if (key == Key.Delete) return InputEvent.Simple(InputKind.DeleteWordForward);
        }

        // Shift+movement → selection. Windows editing conventions:
        // Shift+Backspace is plain backspace, Shift+Delete is cut.
        if (shift && !ctrl && !alt)
        {
            if (key == Key.Left) return InputEvent.Simple(InputKind.SelectLeft);
            if (key == Key.Right) return InputEvent.Simple(InputKind.SelectRight);
            if (key == Key.Up) return InputEvent.Simple(InputKind.SelectLineUp);
            if (key == Key.Down) return InputEvent.Simple(InputKind.SelectLineDown);
            if (key == Key.Home) return InputEvent.Simple(InputKind.SelectToLineStart);
            if (key == Key.End) return InputEvent.Simple(InputKind.SelectToLineEnd);

            if (key == Key.Backspace) return InputEvent.Simple(InputKind.DeleteBackward);
            if (key == Key.Delete) return InputEvent.Simple(InputKind.Cut);
        }

        // Plain keys.
        if (!alt && !ctrl)
        {
            if (key == Key.Tab)
                return InputEvent.Simple(shift ? InputKind.NavigatePrev : InputKind.NavigateNext);
            if (key == Key.Enter)
                return InputEvent.Simple(shift ? InputKind.SecondaryActivate : InputKind.Activate);

            if (!shift)
            {
                if (key == Key.Escape) return InputEvent.Simple(InputKind.Dismiss);
                if (key == Key.Up) return InputEvent.Simple(InputKind.MoveUp);
                if (key == Key.Down) return InputEvent.Simple(InputKind.MoveDown);
                if (key == Key.Left) return InputEvent.Simple(InputKind.MoveLeft);
                if (key == Key.Right) return InputEvent.Simple(InputKind.MoveRight);
                if (key == Key.Home) return InputEvent.Simple(InputKind.MoveToLineStart);
                if (key == Key.End) return InputEvent.Simple(InputKind.MoveToLineEnd);
            }

            if (key == Key.Backspace) return InputEvent.Simple(InputKind.DeleteBackward);
            if (key == Key.Delete) return InputEvent.Simple(InputKind.DeleteForward);
        }

        // No semantic mapping — emit RawKey for the host's shortcut
        // matching. Skip keys that will also arrive as TextInput →
        // TypeChar, to avoid double-firing shortcuts: unmodified printable
        // keys (letters, digits, space, punctuation).
        if (!ctrl && !alt && (key.IsChar(out _) || key == Key.Space))
            return null;
        var (code, mods) = combo.ToFlat();
        return InputEvent.RawKey(code, mods);
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
            | ((keymod & Sdl3.KmodShift) != 0 ? Mods.Shift : Mods.None)
            | ((keymod & Sdl3.KmodGui) != 0 ? Mods.Win : Mods.None);
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
            Sdl3.KeyLShift or Sdl3.KeyRShift => Key.Shift,
            Sdl3.KeyLCtrl or Sdl3.KeyRCtrl => Key.Ctrl,
            Sdl3.KeyLAlt or Sdl3.KeyRAlt => Key.Alt,
            Sdl3.KeyLGui or Sdl3.KeyRGui => Key.Win,
            _ => null,
        };
    }
}
