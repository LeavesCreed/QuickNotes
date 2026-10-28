namespace QuickNotes.Domain;

public sealed record ApplicationSettings
{
    public required long Version { get; init; }
    public int DefaultFontSize { get; init; } = 16;
    public bool GlobalHidden { get; init; }
    public bool NotificationsEnabled { get; init; } = true;
    public TopBarConfig TopBar { get; init; } = TopBarConfig.Default;
    public required DateTimeOffset UpdatedAtUtc { get; init; }

    public static ApplicationSettings CreateDefault(DateTimeOffset now) => new()
    {
        Version = 1,
        DefaultFontSize = 16,
        GlobalHidden = false,
        NotificationsEnabled = true,
        TopBar = TopBarConfig.Default,
        UpdatedAtUtc = now
    };

    public void Validate()
    {
        if (Version < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Version));
        }

        NoteRules.ValidateFontSize(DefaultFontSize);

        if (TopBar is null)
        {
            throw new ArgumentException("Top bar configuration is required.", nameof(TopBar));
        }

        TopBar.Validate();
    }
}
