namespace QuickNotes.Application;

public enum BackupOperationStatus
{
    Succeeded,
    NotConfigured,
    Busy,
    InvalidDirectory,
    Failed,
    NotFound,
    InvalidTarget
}

public sealed record BackupOperationResult
{
    public required BackupOperationStatus Status { get; init; }
    public BackupResult? Backup { get; init; }
    public string? PackagePath { get; init; }
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }
}
