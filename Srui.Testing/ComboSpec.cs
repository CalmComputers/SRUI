namespace Srui.Testing;

/// <summary>Parsing for key combo strings in test input: the config form
/// (<c>"ctrl+shift+t"</c>, see <see cref="KeyCombo.TryParseConfig"/>)
/// extended with compact modifier initials — any segment before the final
/// key made up entirely of the letters <c>c</c> (ctrl), <c>a</c> (alt),
/// <c>s</c> (shift), and <c>w</c> (win) expands letterwise, so
/// <c>"cas+f4"</c> is ctrl+alt+shift+f4, <c>"wa+space"</c> is
/// win+alt+space, and <c>"c+s"</c> is ctrl plus the letter S. The final
/// segment is always the key: a lone <c>"s"</c> taps the letter S.
/// Compact and named forms mix freely.</summary>
public static class ComboSpec
{
    /// <summary>Parse a combo spec. False on an empty spec, an unknown
    /// modifier segment, or an unknown key.</summary>
    public static bool TryParse(string spec, out KeyCombo combo)
    {
        combo = default;
        var parts = spec.Split('+');
        bool ctrl = false, alt = false, shift = false, win = false;

        for (var i = 0; i < parts.Length - 1; i++)
        {
            var part = parts[i].Trim().ToLowerInvariant();
            switch (part)
            {
                case "ctrl" or "control": ctrl = true; break;
                case "alt": alt = true; break;
                case "shift": shift = true; break;
                case "win" or "windows": win = true; break;
                default:
                    if (part.Length == 0 || !part.All(ch => ch is 'c' or 'a' or 's' or 'w'))
                        return false;
                    foreach (var ch in part)
                    {
                        if (ch == 'c') ctrl = true;
                        else if (ch == 'a') alt = true;
                        else if (ch == 'w') win = true;
                        else shift = true;
                    }
                    break;
            }
        }

        if (Key.FromConfigName(parts[^1].Trim().ToLowerInvariant()) is not Key key)
            return false;
        combo = new KeyCombo(key, ctrl, alt, shift, win);
        return true;
    }

    /// <summary>Parse a combo spec, throwing on failure.</summary>
    public static KeyCombo Parse(string spec) =>
        TryParse(spec, out var combo)
            ? combo
            : throw new ArgumentException($"unparseable key combo \"{spec}\"", nameof(spec));
}
