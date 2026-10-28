namespace QuickNotes.Application;

public interface IBackupCoordinator
{
    Task<BackupResult> CreateAsync(
        BackupRequest request,
        CancellationToken cancellationToken = default);
}
