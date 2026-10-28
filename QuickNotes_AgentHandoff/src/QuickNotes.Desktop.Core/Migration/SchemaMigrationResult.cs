namespace QuickNotes.Desktop.Migration;

public enum SchemaMigrationStatus
{
    AlreadyCurrent,
    Migrated,
    Unsupported,
    Failed
}

public sealed record SchemaMigrationResult(
    SchemaMigrationStatus Status,
    long SchemaVersion,
    string? ProtectionSnapshotPath,
    string? ErrorCode,
    string? Message)
{
    public static SchemaMigrationResult AlreadyCurrent(long schemaVersion) =>
        new(SchemaMigrationStatus.AlreadyCurrent, schemaVersion, null, null, "schema_already_current");

    public static SchemaMigrationResult Unsupported(long schemaVersion = 0) =>
        new(SchemaMigrationStatus.Unsupported, schemaVersion, null, "schema_unsupported", "schema_unsupported");

    public static SchemaMigrationResult Migrated(long schemaVersion, string protectionSnapshotPath) =>
        new(SchemaMigrationStatus.Migrated, schemaVersion, protectionSnapshotPath, null, "migration_succeeded");

    public static SchemaMigrationResult Failed(
        string errorCode,
        string? protectionSnapshotPath = null,
        long schemaVersion = 0,
        string? message = null) =>
        new(
            SchemaMigrationStatus.Failed,
            schemaVersion,
            protectionSnapshotPath,
            errorCode,
            string.IsNullOrWhiteSpace(message) ? errorCode : message);
}
