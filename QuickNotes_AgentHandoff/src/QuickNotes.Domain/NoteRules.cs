namespace QuickNotes.Domain;

public static class NoteRules
{
    public const int MinFontSize = 8;
    public const int MaxFontSize = 72;

    public static void ValidateColor(NoteColorMode mode, string? colorHex)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentException("Color mode must be defined.", nameof(mode));
        }

        if (mode == NoteColorMode.FollowCategory)
        {
            if (colorHex is not null)
            {
                throw new ArgumentException(
                    "A follow-category note must not store a manual color.",
                    nameof(colorHex));
            }

            return;
        }

        if (colorHex is not { Length: 7 }
            || colorHex[0] != '#'
            || colorHex.Skip(1).Any(c => !Uri.IsHexDigit(c)))
        {
            throw new ArgumentException(
                "A manual color must use #RRGGBB.",
                nameof(colorHex));
        }
    }

    public static void ValidateFontSize(int? fontSize)
    {
        if (fontSize is { } value && (value < MinFontSize || value > MaxFontSize))
        {
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        }
    }

    public static void ValidateWindowLayout(NoteWindowLayout? layout)
    {
        if (layout is null)
        {
            return;
        }

        if (!double.IsFinite(layout.Left)
            || !double.IsFinite(layout.Top)
            || !double.IsFinite(layout.Width)
            || !double.IsFinite(layout.Height)
            || layout.Width <= 0
            || layout.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(layout));
        }
    }
}
