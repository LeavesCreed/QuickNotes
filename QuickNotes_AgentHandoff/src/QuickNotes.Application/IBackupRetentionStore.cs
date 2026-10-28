namespace QuickNotes.Application;

public interface IBackupRetentionStore
{
    Task<BackupRetentionResult> DeleteAsync(
        string backupDirectory,
        IReadOnlyList<BackupCatalogEntry> packages,
        CancellationToken cancellationToken = default);
}
