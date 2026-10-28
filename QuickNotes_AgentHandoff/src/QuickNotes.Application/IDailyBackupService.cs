namespace QuickNotes.Application;

public interface IDailyBackupService
{
    Task<DailyBackupResult> RunCheckAsync(
        string backupDirectory,
        CancellationToken cancellationToken = default);

    Task RunPeriodicAsync(
        string backupDirectory,
        TimeSpan interval,
        CancellationToken cancellationToken = default);
}
