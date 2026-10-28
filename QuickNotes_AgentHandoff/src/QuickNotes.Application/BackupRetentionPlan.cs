namespace QuickNotes.Application;

public sealed record BackupRetentionPlan(
    IReadOnlyList<BackupCatalogEntry> Keep,
    IReadOnlyList<BackupCatalogEntry> Delete);
