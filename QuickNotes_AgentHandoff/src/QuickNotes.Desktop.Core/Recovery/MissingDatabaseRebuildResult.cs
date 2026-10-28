using QuickNotes.Application;

namespace QuickNotes.Desktop.Recovery;

public sealed record MissingDatabaseRebuildResult
{
    public required MissingDatabaseRebuildStatus Status { get; init; }
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }
    public RestoreCounts? RestoredCounts { get; init; }
    public string? StagingDirectory { get; init; }
    public string? QuarantinePath { get; init; }
    public bool ProtectionSnapshotCreated { get; init; }
    public bool WritesBlocked { get; init; }
    public string? StateWarning { get; init; }
}
