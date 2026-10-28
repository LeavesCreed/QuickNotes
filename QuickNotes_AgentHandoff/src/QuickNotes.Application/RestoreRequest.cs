namespace QuickNotes.Application;

public sealed record RestoreRequest
{
    public required string PackagePath { get; init; }
    public required string CurrentDatabasePath { get; init; }
    public required string BackupDirectory { get; init; }
    public bool AllowPendingDrafts { get; init; }
}
