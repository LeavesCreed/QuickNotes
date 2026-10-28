namespace QuickNotes.Application;

public interface IBackupPackageExtractor
{
    Task<BackupManifest> ExtractAsync(
        string packagePath,
        string destinationDatabasePath,
        CancellationToken cancellationToken = default);
}
