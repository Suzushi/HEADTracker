using System.Windows.Input;

namespace HeadTracker.App.Services;

/// <summary>
/// Parses a user-facing hotkey spec such as "Ctrl+X", "Ctrl+Alt+X" or a bare "F13"
/// into the Win32 <c>RegisterHotKey</c> modifier flags and virtual-key code.
/// The VK is obtained via <see cref="KeyInterop.VirtualKeyFromKey"/>; casting the WPF
/// <see cref="Key"/> enum directly is a bug (Key.C == 46 == VK_DELETE, not VK 'C' == 0x43),
/// which is exactly why the old hardcoded "Alt+C" global re-center never fired in-game.
/// </summary>
internal static class HotkeyParser
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    public const uint ModNoRepeat = 0x4000;

    /// <summary>
    /// Try to parse <paramref name="spec"/>. Returns false for null/empty/unknown tokens,
    /// for more than one non-modifier key, and for a bare letter/digit (which would hijack
    /// that key system-wide); a bare function key (F1..F24) is allowed.
    /// </summary>
    public static bool TryParse(string? spec, out uint modifiers, out int vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(spec))
        {
            return false;
        }

        var parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool haveKey = false;
        Key key = Key.None;
        foreach (var raw in parts)
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= ModControl;
                    continue;
                case "alt":
                    modifiers |= ModAlt;
                    continue;
                case "shift":
                    modifiers |= ModShift;
                    continue;
                case "win":
                case "windows":
                    modifiers |= ModWin;
                    continue;
            }

            // Not a modifier: it must be the (single) key.
            if (haveKey || !TryParseKey(raw, out key))
            {
                return false;
            }
            haveKey = true;
        }

        if (!haveKey)
        {
            return false;
        }

        int code = KeyInterop.VirtualKeyFromKey(key);
        if (code == 0)
        {
            return false;
        }

        // Require a modifier unless the key is a function key, so a plain "X" can never be
        // registered globally (that would steal the X key from every other application).
        bool isFunctionKey = key >= Key.F1 && key <= Key.F24;
        if (modifiers == 0 && !isFunctionKey)
        {
            return false;
        }

        vk = code;
        return true;
    }

    private static bool TryParseKey(string token, out Key key)
    {
        key = Key.None;
        if (token.Length == 1)
        {
            char c = token[0];
            if (c is >= 'a' and <= 'z') { key = Key.A + (c - 'a'); return true; }
            if (c is >= 'A' and <= 'Z') { key = Key.A + (c - 'A'); return true; }
            if (c is >= '0' and <= '9') { key = Key.D0 + (c - '0'); return true; }
            return false;
        }
        return Enum.TryParse<Key>(token, ignoreCase: true, out key) && key != Key.None;
    }
}
