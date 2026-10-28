namespace QuickNotes.Application;

public interface IBackupSnapshotProvider
{
    Task<SnapshotInfo> CreateAsync(
        string destinationDatabasePath,
        CancellationToken cancellationToken = default);
}
