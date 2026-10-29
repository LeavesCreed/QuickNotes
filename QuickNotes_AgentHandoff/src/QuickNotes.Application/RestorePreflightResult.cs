namespace QuickNotes.Application;

public sealed record RestorePreflightResult
{
    public required bool CanProceed { get; init; }
    public BackupManifest? Manifest { get; init; }
    public RestoreCounts? BackupCounts { get; init; }
    public RestoreCounts? CurrentCounts { get; init; }
    public int PendingDraftCount { get; init; }
    public bool RequiresMigration { get; init; }
    public long TargetSchemaVersion { get; init; }
    public string? SourceManifestSha256 { get; init; }
    public string? TargetDatabaseSha256 { get; init; }
    public long RequiredBytes { get; init; }
    public long AvailableBytes { get; init; }
    public RestoreError? Error { get; init; }
}
