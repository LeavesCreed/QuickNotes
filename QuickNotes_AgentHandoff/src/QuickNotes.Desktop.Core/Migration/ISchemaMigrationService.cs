namespace QuickNotes.Desktop.Migration;

public interface ISchemaMigrationService
{
    Task<SchemaMigrationResult> EnsureCurrentAsync(
        long currentSchemaVersion,
        CancellationToken cancellationToken = default);
}
