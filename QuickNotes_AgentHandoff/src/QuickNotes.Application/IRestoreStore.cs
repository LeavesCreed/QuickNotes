namespace QuickNotes.Application;

public interface IRestoreStore
{
    Task<RestoreDatabaseInfo> InspectAsync(
        string databasePath,
        CancellationToken cancellationToken = default);

    Task<string?> TryGetDatabaseHashAsync(
        string databasePath,
        CancellationToken cancellationToken = default);

    Task<RestoreDatabaseInfo> PrepareCurrentAsync(
        string currentDatabasePath,
        CancellationToken cancellationToken = default);

    Task SwitchAsync(
        string stagedDatabasePath,
        string currentDatabasePath,
        string rollbackDatabasePath,
        CancellationToken cancellationToken = default);

    Task RollbackAsync(
        string rollbackDatabasePath,
        string currentDatabasePath,
        string failedDatabasePath,
        CancellationToken cancellationToken = default);
}
