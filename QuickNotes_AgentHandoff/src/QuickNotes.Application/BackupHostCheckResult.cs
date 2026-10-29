namespace QuickNotes.Application;

public enum BackupHostCheckStatus
{
    Completed,
    NotConfigured,
    Busy,
    Failed
}

public sealed record BackupHostCheckResult
{
    public required BackupHostCheckStatus Status { get; init; }
    public DailyBackupResult? Result { get; init; }
    public string? Message { get; init; }
}
