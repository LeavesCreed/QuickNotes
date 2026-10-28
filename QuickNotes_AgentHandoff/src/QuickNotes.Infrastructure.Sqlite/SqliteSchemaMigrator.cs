using System.Globalization;
using Microsoft.Data.Sqlite;
using QuickNotes.Application;

namespace QuickNotes.Infrastructure.Sqlite;

/// <summary>
/// Upgrades a v1 database to the current schema inside a single transaction.
/// The caller is responsible for taking a verified protection snapshot before
/// invoking the migrator.
/// </summary>
public sealed class SqliteSchemaMigrator : ISchemaMigrator
{
    private const long VersionOne = 1;

    private static readonly string VersionOneToTwoDdl = string.Join(
        Environment.NewLine,
        $"ALTER TABLE notes ADD COLUMN {SqliteSchema.ColorModeColumnDdl};",
        $"ALTER TABLE notes ADD COLUMN {SqliteSchema.ColorHexColumnDdl};",
        $"ALTER TABLE notes ADD COLUMN {SqliteSchema.FontSizeColumnDdl};",
        $"ALTER TABLE notes ADD COLUMN {SqliteSchema.WindowLeftColumnDdl};",
        $"ALTER TABLE notes ADD COLUMN {SqliteSchema.WindowTopColumnDdl};",
        $"ALTER TABLE notes ADD COLUMN {SqliteSchema.WindowWidthColumnDdl};",
        $"ALTER TABLE notes ADD COLUMN {SqliteSchema.WindowHeightColumnDdl};",
        SqliteSchema.DatabaseMetaTableDdl,
        SqliteSchema.ApplicationSettingsTableDdl);

    public async Task<SchemaMigrationOutcome> MigrateToCurrentAsync(
        string databasePath,
        bool rotateIdentity,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("Database path must not be empty.", nameof(databasePath));
        }

        var fullPath = Path.GetFullPath(databasePath);
        if (!File.Exists(fullPath))
        {
            throw new SchemaMigrationException(
                "database_missing",
                "The database to migrate does not exist.");
        }

        await using var connection = await new SqliteConnectionFactory(fullPath)
            .OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var fromVersion = await ReadUserVersionAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            if (fromVersion is not (VersionOne or SqliteSchema.CurrentVersion))
            {
                throw new SchemaMigrationException(
                    "schema_unsupported",
                    $"Schema version {fromVersion} is not supported.");
            }

            var migrated = fromVersion == VersionOne;
            var identityRotated = false;
            if (migrated)
            {
                await ExecuteAsync(
                        connection,
                        transaction,
                        VersionOneToTwoDdl,
                        cancellationToken)
                    .ConfigureAwait(false);
                await SqliteSchema.SeedDatabaseMetaAsync(
                        connection,
                        transaction,
                        changeSequence: 1,
                        cancellationToken)
                    .ConfigureAwait(false);
                await SqliteSchema.SeedApplicationSettingsAsync(connection, transaction, cancellationToken)
                    .ConfigureAwait(false);
                await ExecuteAsync(
                        connection,
                        transaction,
                        $"PRAGMA user_version={SqliteSchema.CurrentVersion};",
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (rotateIdentity)
            {
                identityRotated = await RotateIdentityAsync(connection, transaction, cancellationToken)
                    .ConfigureAwait(false);
            }

            var marker = await ReadMarkerAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            await VerifyAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return new SchemaMigrationOutcome(
                fromVersion,
                SqliteSchema.CurrentVersion,
                marker.DatabaseId,
                migrated,
                identityRotated);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SchemaMigrationException)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            throw new SchemaMigrationException(
                "migration_failed",
                $"SQLite error {exception.SqliteErrorCode} (extended {exception.SqliteExtendedErrorCode}) during schema migration.",
                exception);
        }
    }

    private static async Task<long> ReadUserVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static async Task<bool> RotateIdentityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE database_meta
            SET database_id = $database_id,
                change_sequence = change_sequence + 1
            WHERE id = 1;
            """;
        command.Parameters.AddWithValue("$database_id", Guid.NewGuid().ToString("D"));
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected != 1)
        {
            throw new SchemaMigrationException(
                "database_meta_missing",
                "The database_meta row is missing; the database identity cannot be rotated.");
        }

        return true;
    }

    private static async Task<(Guid DatabaseId, long ChangeSequence)> ReadMarkerAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        try
        {
            return await SqliteChangeMarker.ReadAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            throw new SchemaMigrationException(
                "database_meta_missing",
                "The database_meta row is missing after migration.",
                exception);
        }
        catch (FormatException exception)
        {
            throw new SchemaMigrationException(
                "database_meta_invalid",
                "The database_meta database_id is not a valid identifier.",
                exception);
        }
    }

    private static async Task VerifyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "PRAGMA integrity_check;";
            var integrity = Convert.ToString(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
            if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new SchemaMigrationException(
                    "migration_verification_failed",
                    "The migrated database failed integrity_check.");
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "PRAGMA foreign_key_check;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new SchemaMigrationException(
                    "migration_verification_failed",
                    "The migrated database contains foreign key violations.");
            }
        }

        var version = await ReadUserVersionAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        if (version != SqliteSchema.CurrentVersion)
        {
            throw new SchemaMigrationException(
                "migration_verification_failed",
                $"The migrated database reports schema version {version} instead of {SqliteSchema.CurrentVersion}.");
        }
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
