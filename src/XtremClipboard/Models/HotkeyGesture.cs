using Windows.System;

namespace XtremClipboard.Models;

/// <summary>
/// Raccourci clavier global (affichage + Win32).
/// </summary>
public sealed class HotkeyGesture : IEquatable<HotkeyGesture>
{
    public bool Ctrl { get; init; }
    public bool Alt { get; init; }
    public bool Shift { get; init; }
    public bool Win { get; init; }
    public VirtualKey Key { get; init; }

    public bool HasModifier => Ctrl || Alt || Shift || Win;

    /// <summary>Win+V = raccourci historique Windows, géré par WinVSuppress (pas HotkeyService).</summary>
    public bool IsWinV =>
        Win && !Ctrl && !Alt && !Shift && Key is VirtualKey.V;

    public string ToDisplayString()
    {
        var parts = new List<string>(4);
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(KeyToLabel(Key));
        return string.Join("+", parts);
    }

    public static bool TryParse(string? text, out HotkeyGesture? gesture)
    {
        gesture = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return false;

        bool ctrl = false, alt = false, shift = false, win = false;
        VirtualKey? key = null;

        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    ctrl = true;
                    break;
                case "alt":
                    alt = true;
                    break;
                case "shift":
                    shift = true;
                    break;
                case "win":
                case "windows":
                case "cmd":
                    win = true;
                    break;
                default:
                    if (!TryParseKey(part, out var parsed))
                        return false;
                    key = parsed;
                    break;
            }
        }

        if (key is null || !(ctrl || alt || shift || win))
            return false;

        gesture = new HotkeyGesture
        {
            Ctrl = ctrl,
            Alt = alt,
            Shift = shift,
            Win = win,
            Key = key.Value
        };
        return true;
    }

    public bool Equals(HotkeyGesture? other)
    {
        if (other is null) return false;
        return Ctrl == other.Ctrl
               && Alt == other.Alt
               && Shift == other.Shift
               && Win == other.Win
               && Key == other.Key;
    }

    public override bool Equals(object? obj) => Equals(obj as HotkeyGesture);

    public override int GetHashCode() => HashCode.Combine(Ctrl, Alt, Shift, Win, Key);

    public override string ToString() => ToDisplayString();

    private static string KeyToLabel(VirtualKey key) => key switch
    {
        VirtualKey.Number0 => "0",
        VirtualKey.Number1 => "1",
        VirtualKey.Number2 => "2",
        VirtualKey.Number3 => "3",
        VirtualKey.Number4 => "4",
        VirtualKey.Number5 => "5",
        VirtualKey.Number6 => "6",
        VirtualKey.Number7 => "7",
        VirtualKey.Number8 => "8",
        VirtualKey.Number9 => "9",
        _ => key.ToString()
    };

    private static bool TryParseKey(string part, out VirtualKey key)
    {
        if (part.Length == 1)
        {
            var c = char.ToUpperInvariant(part[0]);
            if (c is >= 'A' and <= 'Z')
            {
                key = (VirtualKey)c;
                return true;
            }

            if (c is >= '0' and <= '9')
            {
                key = VirtualKey.Number0 + (c - '0');
                return true;
            }
        }

        return Enum.TryParse(part, ignoreCase: true, out key);
    }
}
