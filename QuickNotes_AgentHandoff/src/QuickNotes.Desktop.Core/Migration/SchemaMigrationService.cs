using System.Globalization;
using System.Text;
using QuickNotes.Application;
using QuickNotes.Desktop.Diagnostics;
using QuickNotes.Desktop.Paths;
using QuickNotes.Infrastructure.Sqlite;

namespace QuickNotes.Desktop.Migration;

/// <summary>
/// Ensures the current database schema is usable before startup enters normal
/// use. A v1 database always gets a verified protection snapshot before the
/// in-place migration; the snapshot is never deleted by this service.
/// </summary>
public sealed class SchemaMigrationService : ISchemaMigrationService
{
    private const long VersionOne = 1;

    private readonly DesktopPaths _paths;
    private readonly ISchemaMigrator _migrator;
    private readonly IRestoreStore _restoreStore;
    private readonly DiagnosticLog _log;
    private readonly TimeProvider _timeProvider;

    public SchemaMigrationService(
        DesktopPaths paths,
        ISchemaMigrator migrator,
        IRestoreStore restoreStore,
        DiagnosticLog log,
        TimeProvider? timeProvider = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _migrator = migrator ?? throw new ArgumentNullException(nameof(migrator));
        _restoreStore = restoreStore ?? throw new ArgumentNullException(nameof(restoreStore));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<SchemaMigrationResult> EnsureCurrentAsync(
        long currentSchemaVersion,
        CancellationToken cancellationToken = default)
    {
        if (currentSchemaVersion == SqliteSchema.CurrentVersion)
        {
            return SchemaMigrationResult.AlreadyCurrent(currentSchemaVersion);
        }

        if (currentSchemaVersion != VersionOne)
        {
            return SchemaMigrationResult.Unsupported(currentSchemaVersion);
        }

        var timestamp = _timeProvider.GetUtcNow().ToString(
            "yyyyMMdd-HHmmss-fff",
            CultureInfo.InvariantCulture);
        var protectionSnapshotPath = Path.Combine(
            _paths.MigrationProtectionDirectory,
            $"notes-v1-{timestamp}.db");
        try
        {
            Directory.CreateDirectory(_paths.MigrationProtectionDirectory);
            var snapshot = await new SqliteSnapshotProvider(_paths.DatabasePath)
                .CreateAsync(protectionSnapshotPath, cancellationToken)
                .ConfigureAwait(false);
            if (snapshot.SchemaVersion != VersionOne)
            {
                _log.TryWrite(
                    "ERROR",
                    "migration",
                    "protection snapshot has an unexpected schema version",
                    "unexpected_protection_schema");
                return SchemaMigrationResult.Failed(
                    "unexpected_protection_schema",
                    protectionSnapshotPath,
                    currentSchemaVersion);
            }

            await _restoreStore
                .InspectAsync(protectionSnapshotPath, cancellationToken)
                .ConfigureAwait(false);
            await _migrator
                .MigrateToCurrentAsync(
                    _paths.DatabasePath,
                    rotateIdentity: true,
                    cancellationToken)
                .ConfigureAwait(false);

            _log.TryWrite("INFO", "migration", "schema migration succeeded");
            return SchemaMigrationResult.Migrated(
                SqliteSchema.CurrentVersion,
                protectionSnapshotPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failure = DescribeFailure(exception);
            _log.TryWrite(
                "ERROR",
                "migration",
                $"schema migration failed ({failure.Detail}); the original database and protection snapshot were preserved",
                failure.ErrorCode);
            return SchemaMigrationResult.Failed(
                "migration_failed",
                protectionSnapshotPath,
                currentSchemaVersion,
                failure.Detail);
        }
    }

    /// <summary>
    /// Preserves the underlying error code (including SQLite failure details)
    /// for diagnostics. The startup-facing error code stays
    /// <c>migration_failed</c>.
    /// </summary>
    private static (string ErrorCode, string Detail) DescribeFailure(Exception exception)
    {
        var errorCode = exception switch
        {
            SchemaMigrationException migrationException => migrationException.ErrorCode,
            RestoreException restoreException => restoreException.ErrorCode,
            _ => exception.GetType().Name
        };

        var detail = new StringBuilder(exception.GetType().Name);
        if (!string.Equals(errorCode, exception.GetType().Name, StringComparison.Ordinal))
        {
            detail.Append(" [").Append(errorCode).Append(']');
        }

        if (!string.IsNullOrWhiteSpace(exception.Message))
        {
            detail.Append(": ").Append(exception.Message);
        }

        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            detail.Append(" -> ").Append(inner.GetType().Name).Append(": ").Append(inner.Message);
        }

        return (errorCode, detail.ToString());
    }
}
