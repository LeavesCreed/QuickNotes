namespace QuickNotes.Application;

public interface IBackupRuntimeStatusStore
{
    Task<BackupRuntimeStatus?> ReadAsync(CancellationToken cancellationToken = default);

    Task WriteAsync(
        BackupRuntimeStatus status,
        CancellationToken cancellationToken = default);
}
