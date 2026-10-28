namespace QuickNotes.Application;

public interface IBackupCatalog
{
    Task<BackupCatalog> ReadForSchedulingAsync(
        string backupDirectory,
        CancellationToken cancellationToken = default);

    Task<BackupCatalog> ReadForRetentionAsync(
        string backupDirectory,
        CancellationToken cancellationToken = default);
}
