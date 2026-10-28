namespace QuickNotes.Application;

public interface IBackupStateStore
{
    Task<BackupScheduleState?> ReadAsync(
        string backupDirectory,
        CancellationToken cancellationToken = default);

    Task WriteAsync(
        string backupDirectory,
        BackupScheduleState state,
        CancellationToken cancellationToken = default);
}
