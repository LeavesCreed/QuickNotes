namespace QuickNotes.Application;

public interface IBackupRetentionPolicy
{
    BackupRetentionPlan CreatePlan(
        IReadOnlyList<BackupCatalogEntry> entries,
        IReadOnlyCollection<string> protectedPackagePaths);
}
