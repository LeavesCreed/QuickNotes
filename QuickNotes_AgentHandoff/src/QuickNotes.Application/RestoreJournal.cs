namespace QuickNotes.Application;

public sealed record RestoreJournal
{
    public const int CurrentFormatVersion = 1;

    public required int FormatVersion { get; init; }
    public required Guid RestoreId { get; init; }
    public required RestoreStage Stage { get; init; }
    public required string CurrentDatabasePath { get; init; }
    public required string StagingDirectory { get; init; }
    public string? ProtectionSnapshotPath { get; init; }
    public string? RollbackPath { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }

    public bool IsTerminal => Stage is RestoreStage.Completed or RestoreStage.Failed;
}
