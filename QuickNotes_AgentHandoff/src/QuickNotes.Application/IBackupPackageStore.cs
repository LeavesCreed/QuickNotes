namespace QuickNotes.Application;

public interface IBackupPackageStore
{
    Task<BackupPackageInfo> PublishAsync(
        string snapshotDatabasePath,
        long schemaVersion,
        BackupRequest request,
        DateTimeOffset createdAtUtc,
        DateTimeOffset createdAtLocal,
        CancellationToken cancellationToken = default);
}
