namespace QuickNotes.Application;

public sealed record DailyBackupResult
{
    public required DailyBackupOutcome Outcome { get; init; }
    public required DateTimeOffset CheckedAtUtc { get; init; }
    public required DateOnly LocalDate { get; init; }
    public string? ExistingDailyPackagePath { get; init; }
    public BackupResult? Backup { get; init; }
    public BackupRetentionResult? Retention { get; init; }
    public BackupError? Error { get; init; }
    public string? StateWarning { get; init; }
    public long DurationMilliseconds { get; init; }
}
