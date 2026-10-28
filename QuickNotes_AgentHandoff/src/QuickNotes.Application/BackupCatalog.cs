namespace QuickNotes.Application;

public sealed record BackupCatalog
{
    public required IReadOnlyList<BackupCatalogEntry> ValidEntries { get; init; }
    public required IReadOnlyList<BackupCatalogIssue> Issues { get; init; }
}
