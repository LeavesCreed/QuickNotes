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
    public RestartEvidence? Restart { get; init; }
    public BackupScenarioEvidence? Backup { get; init; }
    public string? Message { get; init; }
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
