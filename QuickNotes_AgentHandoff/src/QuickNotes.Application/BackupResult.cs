namespace QuickNotes.Application;

public sealed record BackupResult
{
    public required BackupStatus Status { get; init; }
    public string? PackagePath { get; init; }
    public BackupManifest? Manifest { get; init; }
    public BackupError? Error { get; init; }
    public long DurationMilliseconds { get; init; }
}
