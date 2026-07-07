namespace Srui;

/// <summary>Logical input kinds, mirroring the srui_ffi encoding.</summary>
public enum InputKind : uint
{
    NavigateNext = 0,
    NavigatePrev = 1,
    TreeUp = 2,
    TreeDown = 3,
    TreeLeft = 4,
    TreeRight = 5,
    Shortcut = 6,
    Activate = 7,
    SecondaryActivate = 8,
    MoveUp = 9,
    MoveDown = 10,
    MoveLeft = 11,
    MoveRight = 12,
    MoveWordLeft = 13,
    MoveWordRight = 14,
    MoveToLineStart = 15,
    MoveToLineEnd = 16,
    MoveToDocStart = 17,
    MoveToDocEnd = 18,
    MoveLineUp = 19,
    MoveLineDown = 20,
    SelectLeft = 21,
    SelectRight = 22,
    SelectWordLeft = 23,
    SelectWordRight = 24,
    SelectToLineStart = 25,
    SelectToLineEnd = 26,
    SelectToDocStart = 27,
    SelectToDocEnd = 28,
    SelectLineUp = 29,
    SelectLineDown = 30,
    SelectAll = 31,
    TypeChar = 32,
    DeleteBackward = 33,
    DeleteForward = 34,
    DeleteWordBackward = 35,
    DeleteWordForward = 36,
    Copy = 37,
    Cut = 38,
    Paste = 39,
    SpeakFocus = 40,
    Dismiss = 41,
    RawKey = 42,
}

[Flags]
public enum Mods : uint
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
}

/// <summary>Physical key encoding for RawKey inputs.</summary>
public static class Keys
{
    private const uint CharBase = 0x1000_0000;
    private const uint FBase = 0x2000_0000;

    public const uint Enter = 1;
    public const uint Escape = 2;
    public const uint Tab = 3;
    public const uint Space = 4;
    public const uint Up = 5;
    public const uint Down = 6;
    public const uint Left = 7;
    public const uint Right = 8;
    public const uint Home = 9;
    public const uint End = 10;
    public const uint Delete = 11;
    public const uint Backspace = 12;
    public const uint PageUp = 13;
    public const uint PageDown = 14;

    public static uint Char(char c) => CharBase | c;
    public static uint F(byte n) => FBase | n;

    /// <summary>Parse a config-form combo ("ctrl+shift+s") into the flat
    /// key/mods encoding. False when the string does not parse.</summary>
    public static bool TryParse(string combo, out uint key, out Mods mods)
    {
        var ok = NativeMethods.srui_combo_parse(combo, out key, out var rawMods);
        mods = (Mods)rawMods;
        return ok;
    }
}

/// <summary>Phase of a physical key transition.</summary>
public enum KeyPhase
{
    /// <summary>The initial press (not an auto-repeat).</summary>
    Press,
    /// <summary>An auto-repeat while the key is held.</summary>
    Repeat,
    /// <summary>The release.</summary>
    Release,
}

/// <summary>A physical key transition — game-style input, parallel to
/// the logical input stream. Key/Mods use the flat encoding (see
/// Keys).</summary>
public readonly record struct KeyInput(uint Key, Mods Mods, KeyPhase Phase);

/// <summary>A logical input event as it crosses the FFI boundary.</summary>
public readonly record struct InputEvent(InputKind Kind, uint Ch, uint Key, Mods Mods)
{
    public static InputEvent Simple(InputKind kind) => new(kind, 0, 0, Mods.None);
    public static InputEvent TypeChar(char c) => new(InputKind.TypeChar, c, 0, Mods.None);
    public static InputEvent RawKey(uint key, Mods mods) => new(InputKind.RawKey, 0, key, mods);

    public bool IsRawKey(uint key, Mods mods) =>
        Kind == InputKind.RawKey && Key == key && Mods == mods;
}
