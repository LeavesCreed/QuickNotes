namespace QuickNotes.Application;

public enum BackupVerificationStatus
{
    Valid,
    Invalid,
    Missing
}

public sealed record BackupVerificationResult
{
    public required BackupVerificationStatus Status { get; init; }
    public BackupManifest? Manifest { get; init; }
    public BackupError? Error { get; init; }
}
