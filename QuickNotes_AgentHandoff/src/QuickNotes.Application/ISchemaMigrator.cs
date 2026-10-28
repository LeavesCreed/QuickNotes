namespace QuickNotes.Application;

public interface ISchemaMigrator
{
    Task<SchemaMigrationOutcome> MigrateToCurrentAsync(
        string databasePath,
        bool rotateIdentity,
        CancellationToken cancellationToken = default);
}
