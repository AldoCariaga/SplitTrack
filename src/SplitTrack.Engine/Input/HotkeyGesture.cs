namespace SplitTrack.Engine.Input;

/// <summary>A key combination in text form: modifiers and one key joined by '+', e.g. Alt+F10 or Ctrl+Shift+S.</summary>
public sealed record HotkeyGesture(bool Ctrl, bool Alt, bool Shift, bool Win, uint VirtualKey, string KeyName)
{
    private static readonly Dictionary<string, uint> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Insert"] = 0x2D,
        ["Delete"] = 0x2E,
        ["Home"] = 0x24,
        ["End"] = 0x23,
        ["PageUp"] = 0x21,
        ["PageDown"] = 0x22,
        ["Pause"] = 0x13,
        ["ScrollLock"] = 0x91,
    };

    public static HotkeyGesture Parse(string text)
    {
        string[] parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Any(p => p.Length == 0))
        {
            throw new FormatException($"'{text}' is not a hotkey; use e.g. Alt+F10 or Ctrl+Shift+S.");
        }

        bool ctrl = false, alt = false, shift = false, win = false;
        foreach (string modifier in parts[..^1])
        {
            switch (modifier.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    ctrl = true;
                    break;
                case "alt":
                    alt = true;
                    break;
                case "shift":
                    shift = true;
                    break;
                case "win" or "windows":
                    win = true;
                    break;
                default:
                    throw new FormatException($"Unknown modifier '{modifier}' in '{text}' (use Ctrl, Alt, Shift, Win).");
            }
        }

        string key = parts[^1];
        (uint vk, string name, bool needsModifier) = ParseKey(key)
            ?? throw new FormatException($"Unknown key '{key}' in '{text}' (use F1-F24, A-Z, 0-9, Insert, Home, PageUp...).");
        if (needsModifier && !(ctrl || alt || win))
        {
            throw new FormatException($"'{text}' would fire while typing; add Ctrl, Alt or Win.");
        }

        return new HotkeyGesture(ctrl, alt, shift, win, vk, name);
    }

    public override string ToString() =>
        string.Concat(Ctrl ? "Ctrl+" : string.Empty, Alt ? "Alt+" : string.Empty, Shift ? "Shift+" : string.Empty, Win ? "Win+" : string.Empty, KeyName);

    private static (uint Vk, string Name, bool NeedsModifier)? ParseKey(string key)
    {
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]))
        {
            char c = char.ToUpperInvariant(key[0]);
            return (c, c.ToString(), true);
        }

        if (key.Length >= 2 && key[0] is 'F' or 'f' && int.TryParse(key[1..], out int n) && n is >= 1 and <= 24)
        {
            return ((uint)(0x70 + n - 1), $"F{n}", false);
        }

        return NamedKeys.TryGetValue(key, out uint vk) ? (vk, NamedKeys.Keys.First(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)), false) : null;
    }
}
