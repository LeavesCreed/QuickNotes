namespace QuickNotes.Desktop.Paths;

public sealed record DesktopPaths
{
    public const string DatabaseFileName = "notes.db";
    public const string StateFileName = "application-state.json";
    public const string RestoreJournalFileName = "recovery-journal.json";
    public const string DeviceSettingsFileName = "device-settings.json";
    public const string BackupRuntimeStatusFileName = "backup-status.json";
    public const string DraftRestoreBoundaryFileName = "draft-restore-boundary.json";

    public required string DataRoot { get; init; }

    public string DatabasePath => Path.Combine(DataRoot, DatabaseFileName);
    public string DraftDirectory => Path.Combine(DataRoot, "drafts");
    public string LogDirectory => Path.Combine(DataRoot, "logs");
    public string StatePath => Path.Combine(DataRoot, StateFileName);
    public string RestoreJournalPath => Path.Combine(DataRoot, RestoreJournalFileName);
    public string DeviceSettingsPath => Path.Combine(DataRoot, DeviceSettingsFileName);
    public string BackupRuntimeStatusPath => Path.Combine(DataRoot, BackupRuntimeStatusFileName);
    public string DraftRestoreBoundaryPath => Path.Combine(DataRoot, DraftRestoreBoundaryFileName);
    public string MigrationProtectionDirectory => Path.Combine(DataRoot, "migration-protection");

    public static DesktopPaths CreateDefault() =>
        Create(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QuickNotes"));

    public static DesktopPaths Create(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException("Data root must not be empty.", nameof(dataRoot));
        }

        return new DesktopPaths
        {
            DataRoot = Path.GetFullPath(dataRoot)
        };
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(DraftDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}
