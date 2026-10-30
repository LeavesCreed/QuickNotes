using QuickNotes.Application;

namespace QuickNotes.C6.Harness;

/// <summary>
/// One JSON line emitted by a realized C6 scenario. It contains only stages,
/// status values, counts, and timings; note bodies and machine paths are never
/// included. <see cref="Passed"/> means the observed result matched the
/// scenario's expected composition (success without a fault, safe failure with
/// one). It is not a performance budget claim.
/// </summary>
public sealed record HarnessScenarioResult
{
    public required string Scenario { get; init; }
    public required string Status { get; init; }
    public required bool Passed { get; init; }
    public string? FailureStage { get; init; }
    public string? ErrorCode { get; init; }
    public SaveStatus? SaveStatus { get; init; }
    public bool? SavedReported { get; init; }
    public DraftDisposition? DraftDisposition { get; init; }
    public int? Attempts { get; init; }
    public bool? DraftSaved { get; init; }
    public bool? DraftRetained { get; init; }
    public bool? NoteSaved { get; init; }
    public bool? DatabaseReadable { get; init; }
    public int? NoteCount { get; init; }
    public int? TotalNoteCount { get; init; }
    public RestartEvidence? Restart { get; init; }
    public BackupScenarioEvidence? Backup { get; init; }
    public RestartProbeEvidence? RestartProbe { get; init; }
    public RestoreStatus? RestoreStatus { get; init; }
    public bool? WritesBlocked { get; init; }
    public bool? RolledBack { get; init; }
    public InstanceEvidence? Instance { get; init; }
    public RestorePauseEvidence? Pause { get; init; }
    public string? Message { get; init; }
}

/// <summary>
/// Single-instance ownership observed by a restore run. The harness holds the
/// real production lock and activation pipe for the disposable data root; a
/// second normal instance must either activate this owner or fail explicitly.
/// </summary>
public sealed record InstanceEvidence
{
    public required string StartResult { get; init; }
    public required int ActivationCount { get; init; }
}

/// <summary>
/// Bounded pause at a restore checkpoint. The marker is written before the
/// pause starts; <see cref="Released"/> is true only when the release file was
/// observed before the timeout.
/// </summary>
public sealed record RestorePauseEvidence
{
    public required string Checkpoint { get; init; }
    public required bool MarkerWritten { get; init; }
    public required bool Released { get; init; }
    public long WaitMilliseconds { get; init; }
}

/// <summary>
/// A fresh-service reopen performed after the fault. It is deliberately
/// narrower than Task 3's process-kill/restart matrix: it proves the on-disk
/// database and draft remain readable by new service instances.
/// </summary>
public sealed record RestartEvidence
{
    public required string Method { get; init; }
    public required bool Passed { get; init; }
    public required bool DatabaseReadable { get; init; }
    public required bool NotePresent { get; init; }
    public bool? DraftPresent { get; init; }
    public int? NoteCount { get; init; }
    public string? Failure { get; init; }
}

public sealed record BackupScenarioEvidence
{
    public required BackupStatus Status { get; init; }
    public BackupErrorCode? ErrorCode { get; init; }
    public required bool NewPackagePublished { get; init; }
    public required bool ExistingPackagesIntact { get; init; }
    public required int PackageCountBefore { get; init; }
    public required int PackageCountAfter { get; init; }
    public required int TemporaryResidueCount { get; init; }
    public string? NewPackageFileName { get; init; }
    public long DurationMilliseconds { get; init; }
}

/// <summary>
/// Startup/restart evidence produced by <c>QuickNotes.C6.Harness restart</c>
/// after a process kill. It reports the real startup assessment, counts, and
/// recovery artifacts only; note bodies and machine paths are never included.
/// </summary>
public sealed record RestartProbeEvidence
{
    public required string StartupMode { get; init; }
    public string? StartupReason { get; init; }
    public string? ErrorCode { get; init; }
    public required bool WritesBlocked { get; init; }
    public required bool DatabaseExists { get; init; }
    public required bool DatabaseReadable { get; init; }
    public long? SchemaVersionBeforeStartup { get; init; }
    public long? SchemaVersionAfterStartup { get; init; }
    public required int NoteCount { get; init; }
    public int TotalNoteCount { get; init; }
    public long? MaxNoteVersion { get; init; }
    public Guid? TargetNoteId { get; init; }
    public bool? TargetNoteExists { get; init; }
    public long? TargetNoteVersion { get; init; }
    public required int DraftCountBeforeStartup { get; init; }
    public required int DraftCountAfterStartup { get; init; }
    /// <summary>
    /// Draft count observed after startup, not a maximum across the run. A
    /// startup that removed a draft must never look like a retained draft.
    /// </summary>
    public required int DraftCount { get; init; }
    public string? RestoreJournalStage { get; init; }
    public required IReadOnlyList<RecoveryArtifactEvidence> RecoveryArtifacts { get; init; }
    public required IReadOnlyList<BackupPackageEvidence> BackupPackages { get; init; }
}

public sealed record RecoveryArtifactEvidence(
    string Kind,
    string Name,
    bool IsDirectory);

public sealed record BackupPackageEvidence(
    string FileName,
    bool Valid,
    long SizeBytes);
