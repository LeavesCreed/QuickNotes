namespace QuickNotes.Application;

public sealed record RestoreResult
{
    public required RestoreStatus Status { get; init; }
    public BackupManifest? Manifest { get; init; }
    public RestoreCounts? RestoredCounts { get; init; }
    public string? ProtectionSnapshotPath { get; init; }
    public RestoreError? Error { get; init; }
    public bool RolledBack { get; init; }
    public bool WritesBlocked { get; init; }
    public string? RecoveryHint { get; init; }
    public long DurationMilliseconds { get; init; }
}
