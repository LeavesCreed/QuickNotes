using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using QuickNotes.Application;

namespace QuickNotes.Infrastructure.Sqlite;

public sealed class SqliteChangeDetector : IBackupChangeDetector
{
    private const int BusyTimeoutMilliseconds = 250;
    private const string ChangeMarkerUnavailable = "change_marker_unavailable";

    private readonly string _sourceDatabasePath;

    public SqliteChangeDetector(string sourceDatabasePath)
    {
        if (string.IsNullOrWhiteSpace(sourceDatabasePath))
        {
            throw new ArgumentException(
                "Source database path must not be empty.",
                nameof(sourceDatabasePath));
        }

        _sourceDatabasePath = sourceDatabasePath;
    }

    public async Task<string> GetFingerprintAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(_sourceDatabasePath))
        {
            throw new BackupException(
                "change_detection",
                BackupErrorCodes.SourceUnavailable,
                "The source database does not exist.");
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _sourceDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 1
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"PRAGMA busy_timeout={BusyTimeoutMilliseconds};";
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var transaction = connection.BeginTransaction(
                IsolationLevel.Serializable,
                deferred: true);
            var marker = await ReadMarkerAsync(connection, transaction, cancellationToken);
            await transaction.CommitAsync(CancellationToken.None);
            return marker.ToFingerprint();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            throw new BackupException(
                "change_detection",
                BackupErrorCodes.SourceUnavailable,
                FormatSqliteError(exception),
                exception,
                IsBusy(exception));
        }
    }

    private static async Task<DataChangeMarker> ReadMarkerAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var schemaVersion = await ScalarLongAsync(
            connection,
            transaction,
            "PRAGMA user_version;",
            cancellationToken);
        var metaTableCount = await ScalarLongAsync(
            connection,
            transaction,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'database_meta';",
            cancellationToken);
        if (metaTableCount == 0)
        {
            throw ChangeMarkerMissing();
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT database_id, change_sequence
            FROM database_meta
            WHERE id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw ChangeMarkerMissing();
        }

        if (!Guid.TryParse(reader.GetString(0), out var databaseId))
        {
            throw ChangeMarkerMissing("database_meta contains an invalid database_id.");
        }

        return new DataChangeMarker(schemaVersion, databaseId, reader.GetInt64(1));
    }

    private static BackupException ChangeMarkerMissing(
        string message = "database_meta is missing.") =>
        new("change_detection", ChangeMarkerUnavailable, message);

    private static async Task<long> ScalarLongAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    private static bool IsBusy(SqliteException exception)
    {
        var extended = exception.SqliteExtendedErrorCode;
        var primary = extended != 0 ? extended & 0xFF : exception.SqliteErrorCode;
        return primary is 5 or 6;
    }

    private static string FormatSqliteError(SqliteException exception)
    {
        var extended = exception.SqliteExtendedErrorCode;
        var primary = extended != 0 ? extended & 0xFF : exception.SqliteErrorCode;
        return $"SQLite error {primary} (extended {extended}) during change detection.";
    }
}
