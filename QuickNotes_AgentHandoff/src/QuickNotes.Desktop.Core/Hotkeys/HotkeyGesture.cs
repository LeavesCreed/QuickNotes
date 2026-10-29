namespace QuickNotes.Desktop.Hotkeys;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Win = 8
}

public sealed record HotkeyGesture(
    HotkeyModifiers Modifiers,
    uint VirtualKey,
    string KeyName)
{
    public string Display
    {
        get
        {
            var parts = new List<string>();
            if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
            if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
            parts.Add(KeyName);
            return string.Join("+", parts);
        }
    }

    public static bool TryParse(
        string? text,
        out HotkeyGesture? gesture,
        out string? error)
    {
        gesture = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "empty_gesture";
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            error = "missing_modifier";
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        string? keyName = null;
        foreach (var part in parts)
        {
            var lower = part.ToLowerInvariant();
            switch (lower)
            {
                case "ctrl":
                case "control":
                    modifiers |= HotkeyModifiers.Control;
                    continue;
                case "alt":
                    modifiers |= HotkeyModifiers.Alt;
                    continue;
                case "shift":
                    modifiers |= HotkeyModifiers.Shift;
                    continue;
                case "win":
                case "windows":
                    modifiers |= HotkeyModifiers.Win;
                    continue;
            }

            if (keyName is not null)
            {
                error = "multiple_keys";
                return false;
            }

            keyName = part;
        }

        if (modifiers == HotkeyModifiers.None)
        {
            error = "missing_modifier";
            return false;
        }

        if (keyName is null || !TryMapKey(keyName, out var virtualKey, out var canonicalName))
        {
            error = "unsupported_key";
            return false;
        }

        gesture = new HotkeyGesture(modifiers, virtualKey, canonicalName);
        return true;
    }

    public static HotkeyGesture Parse(string text)
    {
        if (!TryParse(text, out var gesture, out var error))
        {
            throw new FormatException($"Invalid hotkey gesture ({error}).");
        }

        return gesture!;
    }

    private static bool TryMapKey(string keyName, out uint virtualKey, out string canonicalName)
    {
        virtualKey = 0;
        canonicalName = keyName.ToUpperInvariant();
        if (canonicalName.Length == 1)
        {
            var ch = char.ToUpperInvariant(canonicalName[0]);
            if (ch is >= 'A' and <= 'Z')
            {
                virtualKey = ch;
                return true;
            }

            if (ch is >= '0' and <= '9')
            {
                virtualKey = ch;
                return true;
            }
        }

        if (canonicalName.StartsWith('F')
            && int.TryParse(canonicalName[1..], out var functionNumber)
            && functionNumber is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x70 + functionNumber - 1);
            canonicalName = $"F{functionNumber}";
            return true;
        }

        var named = canonicalName switch
        {
            "SPACE" => (0x20u, "Space"),
            "ENTER" or "RETURN" => (0x0Du, "Enter"),
            "ESC" or "ESCAPE" => (0x1Bu, "Escape"),
            "TAB" => (0x09u, "Tab"),
            "BACKSPACE" => (0x08u, "Backspace"),
            "DELETE" or "DEL" => (0x2Eu, "Delete"),
            "INSERT" or "INS" => (0x2Du, "Insert"),
            "HOME" => (0x24u, "Home"),
            "END" => (0x23u, "End"),
            "PAGEUP" or "PGUP" => (0x21u, "PageUp"),
            "PAGEDOWN" or "PGDN" => (0x22u, "PageDown"),
            "LEFT" => (0x25u, "Left"),
            "UP" => (0x26u, "Up"),
            "RIGHT" => (0x27u, "Right"),
            "DOWN" => (0x28u, "Down"),
            "PRINTSCREEN" or "PRTSC" => (0x2Cu, "PrintScreen"),
            "PAUSE" => (0x13u, "Pause"),
            _ => (0u, string.Empty)
        };
        if (named.Item1 == 0)
        {
            return false;
        }

        virtualKey = named.Item1;
        canonicalName = named.Item2;
        return true;
    }
}
