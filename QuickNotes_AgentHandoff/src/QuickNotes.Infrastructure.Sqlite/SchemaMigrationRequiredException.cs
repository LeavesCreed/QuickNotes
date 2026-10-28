namespace QuickNotes.Infrastructure.Sqlite;

public sealed class SchemaMigrationRequiredException : Exception
{
    public SchemaMigrationRequiredException(long currentVersion, long supportedVersion)
        : base(
            $"Database schema version {currentVersion} requires migration to version {supportedVersion}.")
    {
        CurrentVersion = currentVersion;
        SupportedVersion = supportedVersion;
    }

    public long CurrentVersion { get; }
    public long SupportedVersion { get; }
}
