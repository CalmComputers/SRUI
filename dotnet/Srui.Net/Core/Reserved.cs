namespace Srui.Core;

/// <summary>Reserved key combos — the combos the framework itself consumes.
/// Key bindings are host policy; the core's only contribution is naming the
/// combos it will never let a binding see, each with a spoken reason for
/// bind dialogs to announce. Everything else — including combos like
/// Ctrl+Tab or Escape that merely might collide with a cancel widget — is
/// at most a soft conflict for the host to warn about (see
/// <see cref="Role.ReservesKey"/> for the per-widget-role side).</summary>
internal static class Reserved
{
    /// <summary>If the combo is categorically unbindable, the spoken
    /// reason why; otherwise null.</summary>
    public static string? ReservedReason(in KeyCombo combo)
    {
        // Plain Tab / Shift+Tab: the focus ring must always work.
        if (combo.Key == Key.Tab && !combo.Ctrl && !combo.Alt)
            return "Tab is reserved for moving between widgets";
        // Alt+letter: widget mnemonics. Alt+arrows: hierarchy navigation.
        if (combo.Alt && !combo.Ctrl && !combo.Shift)
        {
            if (combo.Key.IsChar(out var c) && char.IsAsciiLetter(c))
                return "Alt plus a letter is reserved for widget shortcuts";
            if (combo.Key == Key.Up || combo.Key == Key.Down
                || combo.Key == Key.Left || combo.Key == Key.Right)
                return "Alt plus arrows is reserved for tree navigation";
        }
        return null;
    }
}
