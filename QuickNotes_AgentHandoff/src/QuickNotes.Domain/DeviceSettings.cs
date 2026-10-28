namespace QuickNotes.Domain;

public sealed record DeviceSettings
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public string? BackupDirectory { get; init; }
    public bool StartupRegistrationEnabled { get; init; }
    public HotkeySettings? Hotkeys { get; init; } = HotkeySettings.Default;
    public IReadOnlyDictionary<Guid, string> DisplayBindings { get; init; }
        = new Dictionary<Guid, string>();

    public void Validate()
    {
        if (FormatVersion != CurrentFormatVersion)
        {
            throw new ArgumentException(
                $"Device settings format version must be {CurrentFormatVersion}.",
                nameof(FormatVersion));
        }

        if (BackupDirectory is not null
            && (string.IsNullOrWhiteSpace(BackupDirectory)
                || !Path.IsPathFullyQualified(BackupDirectory)))
        {
            throw new ArgumentException(
                "Backup directory must be an absolute path.",
                nameof(BackupDirectory));
        }

        if (DisplayBindings is null)
        {
            throw new ArgumentException(
                "Display bindings are required.",
                nameof(DisplayBindings));
        }

        foreach (var (noteId, displayId) in DisplayBindings)
        {
            if (noteId == Guid.Empty || string.IsNullOrWhiteSpace(displayId))
            {
                throw new ArgumentException(
                    "Display bindings must use non-empty note and display identifiers.",
                    nameof(DisplayBindings));
            }
        }

        Hotkeys?.Validate();
    }
}
