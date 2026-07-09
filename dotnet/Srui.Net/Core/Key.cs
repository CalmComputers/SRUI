namespace Srui;

/// <summary>A physical key, independent of modifier state. Wraps the flat
/// encoding shared with the <see cref="Keys"/> constants: named keys are
/// small integers, characters are 0x1000_0000 | codepoint, F-keys are
/// 0x2000_0000 | n.</summary>
public readonly record struct Key(uint Code)
{
    private const uint CharBase = 0x1000_0000;
    private const uint FBase = 0x2000_0000;
    private const uint BaseMask = 0xF000_0000;

    public static readonly Key Enter = new(1);
    public static readonly Key Escape = new(2);
    public static readonly Key Tab = new(3);
    public static readonly Key Space = new(4);
    public static readonly Key Up = new(5);
    public static readonly Key Down = new(6);
    public static readonly Key Left = new(7);
    public static readonly Key Right = new(8);
    public static readonly Key Home = new(9);
    public static readonly Key End = new(10);
    public static readonly Key Delete = new(11);
    public static readonly Key Backspace = new(12);
    public static readonly Key PageUp = new(13);
    public static readonly Key PageDown = new(14);
    public static readonly Key MediaPlayPause = new(15);
    public static readonly Key MediaNextTrack = new(16);
    public static readonly Key MediaPreviousTrack = new(17);
    public static readonly Key MediaStop = new(18);

    /// <summary>A character key. Callers normalize to ASCII lowercase where
    /// the input map does (typing, shortcuts).</summary>
    public static Key Char(char c) => new(CharBase | c);

    /// <summary>A character key from a full Unicode codepoint (astral
    /// characters ride the flat encoding unchanged).</summary>
    public static Key CharCode(uint codepoint) => new(CharBase | codepoint);

    /// <summary>F1..F12.</summary>
    public static Key F(byte n) => new(FBase | n);

    public bool IsChar(out char c)
    {
        if ((Code & BaseMask) == CharBase)
        {
            c = (char)(Code & ~BaseMask);
            return true;
        }
        c = default;
        return false;
    }

    public bool IsF(out byte n)
    {
        if ((Code & BaseMask) == FBase)
        {
            n = (byte)(Code & ~BaseMask);
            return true;
        }
        n = default;
        return false;
    }

    /// <summary>Human-readable name for speech: "enter", "page up", "f4", "s".</summary>
    public string DisplayName()
    {
        if (IsChar(out var c))
            return char.ToLowerInvariant(c).ToString();
        if (IsF(out var n))
            return $"f{n}";
        return Code switch
        {
            1 => "enter",
            2 => "escape",
            3 => "tab",
            4 => "space",
            5 => "up",
            6 => "down",
            7 => "left",
            8 => "right",
            9 => "home",
            10 => "end",
            11 => "delete",
            12 => "backspace",
            13 => "page up",
            14 => "page down",
            15 => "play pause",
            16 => "next track",
            17 => "previous track",
            18 => "stop",
            _ => "?",
        };
    }

    /// <summary>Canonical config name: "pageup", "f4", "s". "?" for keys
    /// with no config form.</summary>
    public string ConfigName()
    {
        if (IsChar(out var c))
            return char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c).ToString() : "?";
        if (IsF(out var n))
            return n is >= 1 and <= 12 ? $"f{n}" : "f?";
        return Code switch
        {
            1 => "enter",
            2 => "escape",
            3 => "tab",
            4 => "space",
            5 => "up",
            6 => "down",
            7 => "left",
            8 => "right",
            9 => "home",
            10 => "end",
            11 => "delete",
            12 => "backspace",
            13 => "pageup",
            14 => "pagedown",
            15 => "playpause",
            16 => "nexttrack",
            17 => "previoustrack",
            18 => "mediastop",
            _ => "?",
        };
    }

    /// <summary>Config name → key. Accepts canonical names and common
    /// aliases ("esc", "del", "pgup"). Null when unknown.</summary>
    public static Key? FromConfigName(string s)
    {
        if (s.Length == 1 && char.IsAsciiLetterOrDigit(s[0]))
            return Char(char.ToLowerInvariant(s[0]));
        if (s.Length >= 2 && s[0] == 'f' && byte.TryParse(s.AsSpan(1), out var n) && n is >= 1 and <= 12)
            return F(n);
        return s switch
        {
            "enter" or "return" => Enter,
            "escape" or "esc" => Escape,
            "tab" => Tab,
            "space" => Space,
            "up" => Up,
            "down" => Down,
            "left" => Left,
            "right" => Right,
            "home" => Home,
            "end" => End,
            "delete" or "del" => Delete,
            "backspace" => Backspace,
            "pageup" or "pgup" => PageUp,
            "pagedown" or "pgdn" or "pgdown" => PageDown,
            "playpause" => MediaPlayPause,
            "nexttrack" => MediaNextTrack,
            "previoustrack" or "prevtrack" => MediaPreviousTrack,
            "mediastop" or "stop" => MediaStop,
            _ => null,
        };
    }
}

/// <summary>A key combined with modifier state. Carries both string forms
/// (spoken and config) and the framework's reservation verdict, so a bind
/// dialog needs nothing else.</summary>
public readonly record struct KeyCombo(Key Key, bool Ctrl, bool Alt, bool Shift)
{
    public static KeyCombo Plain(Key key) => new(key, false, false, false);
    public static KeyCombo WithCtrl(Key key) => new(key, true, false, false);
    public static KeyCombo WithAlt(Key key) => new(key, false, true, false);
    public static KeyCombo WithShift(Key key) => new(key, false, false, true);
    public static KeyCombo CtrlShift(Key key) => new(key, true, false, true);
    public static KeyCombo AltShift(Key key) => new(key, false, true, true);

    /// <summary>From the flat (key, mods) encoding used by RawKey inputs.</summary>
    public static KeyCombo FromFlat(uint key, Mods mods) => new(
        new Key(key),
        (mods & Mods.Ctrl) != 0,
        (mods & Mods.Alt) != 0,
        (mods & Mods.Shift) != 0);

    public (uint Key, Mods Mods) ToFlat() => (
        Key.Code,
        (Ctrl ? Mods.Ctrl : Mods.None) | (Alt ? Mods.Alt : Mods.None) | (Shift ? Mods.Shift : Mods.None));

    /// <summary>Reverse-map a logical input to the combo that would produce
    /// it under the default input map. Null for synthetic inputs
    /// (SpeakFocus) with no single physical combo.</summary>
    public static KeyCombo? FromInput(in InputEvent input)
    {
        var ch = (char)input.Ch;
        return input.Kind switch
        {
            InputKind.NavigateNext => Plain(Key.Tab),
            InputKind.NavigatePrev => WithShift(Key.Tab),
            InputKind.TreeUp => WithAlt(Key.Up),
            InputKind.TreeDown => WithAlt(Key.Down),
            InputKind.TreeLeft => WithAlt(Key.Left),
            InputKind.TreeRight => WithAlt(Key.Right),
            InputKind.Shortcut => WithAlt(Key.CharCode(ToAsciiLowerCode(input.Ch))),

            InputKind.Activate => Plain(Key.Enter),
            InputKind.SecondaryActivate => WithShift(Key.Enter),
            InputKind.MoveUp or InputKind.MoveLineUp => Plain(Key.Up),
            InputKind.MoveDown or InputKind.MoveLineDown => Plain(Key.Down),
            InputKind.MoveLeft => Plain(Key.Left),
            InputKind.MoveRight => Plain(Key.Right),
            InputKind.MoveWordLeft => WithCtrl(Key.Left),
            InputKind.MoveWordRight => WithCtrl(Key.Right),
            InputKind.MoveToLineStart => Plain(Key.Home),
            InputKind.MoveToLineEnd => Plain(Key.End),
            InputKind.MoveToDocStart => WithCtrl(Key.Home),
            InputKind.MoveToDocEnd => WithCtrl(Key.End),

            InputKind.SelectLeft => WithShift(Key.Left),
            InputKind.SelectRight => WithShift(Key.Right),
            InputKind.SelectWordLeft => CtrlShift(Key.Left),
            InputKind.SelectWordRight => CtrlShift(Key.Right),
            InputKind.SelectToLineStart => WithShift(Key.Home),
            InputKind.SelectToLineEnd => WithShift(Key.End),
            InputKind.SelectToDocStart => CtrlShift(Key.Home),
            InputKind.SelectToDocEnd => CtrlShift(Key.End),
            InputKind.SelectLineUp => WithShift(Key.Up),
            InputKind.SelectLineDown => WithShift(Key.Down),
            InputKind.SelectAll => WithCtrl(Key.Char('a')),

            InputKind.TypeChar when ch == ' ' => Plain(Key.Space),
            InputKind.TypeChar => Plain(Key.CharCode(ToAsciiLowerCode(input.Ch))),
            InputKind.DeleteBackward => Plain(Key.Backspace),
            InputKind.DeleteForward => Plain(Key.Delete),
            InputKind.DeleteWordBackward => WithCtrl(Key.Backspace),
            InputKind.DeleteWordForward => WithCtrl(Key.Delete),

            InputKind.Copy => WithCtrl(Key.Char('c')),
            InputKind.Cut => WithCtrl(Key.Char('x')),
            InputKind.Paste => WithCtrl(Key.Char('v')),

            InputKind.Dismiss => Plain(Key.Escape),

            InputKind.RawKey => FromFlat(input.Key, input.Mods),

            _ => null, // SpeakFocus — synthetic, no physical key
        };
    }

    private static uint ToAsciiLowerCode(uint c) => c is >= 'A' and <= 'Z' ? c + 32 : c;

    /// <summary>Whether this combo matches the given logical input.</summary>
    public bool MatchesInput(in InputEvent input) => FromInput(input) == this;

    /// <summary>Spoken form: "control alt shift s". Modifier order:
    /// control, alt, shift, then the key. Space-separated, lowercase.</summary>
    public string DisplayName()
    {
        var result = new System.Text.StringBuilder();
        if (Ctrl) result.Append("control ");
        if (Alt) result.Append("alt ");
        if (Shift) result.Append("shift ");
        result.Append(Key.DisplayName());
        return result.ToString();
    }

    public override string ToString() => DisplayName();

    /// <summary>Compact config form: "ctrl+shift+s", "alt+f2", "enter".
    /// Modifier order: ctrl, alt, shift, then key. Plus-separated, lowercase.</summary>
    public string ToConfigString()
    {
        var result = new System.Text.StringBuilder();
        if (Ctrl) result.Append("ctrl+");
        if (Alt) result.Append("alt+");
        if (Shift) result.Append("shift+");
        result.Append(Key.ConfigName());
        return result.ToString();
    }

    /// <summary>Parse a config string like "ctrl+shift+s". False on an
    /// empty string, an unknown key, or more than one key part.</summary>
    public static bool TryParseConfig(string s, out KeyCombo combo)
    {
        combo = default;
        bool ctrl = false, alt = false, shift = false;
        string? keyPart = null;

        foreach (var raw in s.Split('+'))
        {
            var part = raw.Trim().ToLowerInvariant();
            switch (part)
            {
                case "ctrl" or "control": ctrl = true; break;
                case "alt": alt = true; break;
                case "shift": shift = true; break;
                default:
                    if (keyPart is not null)
                        return false; // multiple key parts
                    keyPart = part;
                    break;
            }
        }

        if (keyPart is null)
            return false;
        var key = Key.FromConfigName(keyPart);
        if (key is null)
            return false;
        combo = new KeyCombo(key.Value, ctrl, alt, shift);
        return true;
    }

    /// <summary>If the combo is categorically unbindable — the framework
    /// itself consumes it — the spoken reason why; otherwise null. Key
    /// bindings are host policy, and this is the engine's whole
    /// contribution to hard conflicts: everything else, including combos
    /// like Ctrl+Tab or Escape that merely might collide with a cancel
    /// widget, is at most a soft conflict for the host to warn about (see
    /// <see cref="Widget.ReservesKey"/> for the per-widget side).</summary>
    public string? ReservedReason
    {
        get
        {
            // Plain Tab / Shift+Tab: the focus ring must always work.
            if (Key == Key.Tab && !Ctrl && !Alt)
                return "Tab is reserved for moving between widgets";
            // Alt+letter: widget mnemonics. Alt+arrows: hierarchy navigation.
            if (Alt && !Ctrl && !Shift)
            {
                if (Key.IsChar(out var c) && char.IsAsciiLetter(c))
                    return "Alt plus a letter is reserved for widget shortcuts";
                if (Key == Key.Up || Key == Key.Down
                    || Key == Key.Left || Key == Key.Right)
                    return "Alt plus arrows is reserved for tree navigation";
            }
            return null;
        }
    }
}
