namespace QuickNotes.Application;

public sealed record SchemaMigrationOutcome(
    long FromSchemaVersion,
    long ToSchemaVersion,
    Guid DatabaseId,
    bool Migrated,
    bool IdentityRotated);
