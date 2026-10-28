using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using QuickNotes.Application;

namespace QuickNotes.Infrastructure.Sqlite;

public sealed class SqliteChangeDetector : IBackupChangeDetector
{
    private const int BusyTimeoutMilliseconds = 250;

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
            var schemaVersion = await ScalarLongAsync(
                connection,
                transaction,
                "PRAGMA user_version;",
                cancellationToken);
            var maximumModified = await ScalarStringAsync(
                connection,
                transaction,
                "SELECT COALESCE(MAX(modified_at_utc), '') FROM notes;",
                cancellationToken);
            var noteCount = await ScalarLongAsync(
                connection,
                transaction,
                "SELECT COUNT(*) FROM notes;",
                cancellationToken);
            var tagCount = await ScalarLongAsync(
                connection,
                transaction,
                "SELECT COUNT(*) FROM tags;",
                cancellationToken);
            var noteTagCount = await ScalarLongAsync(
                connection,
                transaction,
                "SELECT COUNT(*) FROM note_tags;",
                cancellationToken);
            var categoryCount = await ScalarLongAsync(
                connection,
                transaction,
                "SELECT COUNT(*) FROM categories;",
                cancellationToken);
            var tagNameLengths = await ScalarLongAsync(
                connection,
                transaction,
                "SELECT COALESCE(SUM(LENGTH(name)), 0) FROM tags;",
                cancellationToken);
            var categoryNameLengths = await ScalarLongAsync(
                connection,
                transaction,
                "SELECT COALESCE(SUM(LENGTH(name)), 0) FROM categories;",
                cancellationToken);
            await transaction.CommitAsync(CancellationToken.None);

            return string.Format(
                CultureInfo.InvariantCulture,
                "schema={0};notes={1};tags={2};note_tags={3};categories={4};tag_name_lengths={5};category_name_lengths={6};modified={7}",
                schemaVersion,
                noteCount,
                tagCount,
                noteTagCount,
                categoryCount,
                tagNameLengths,
                categoryNameLengths,
                maximumModified);
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

    private static async Task<string> ScalarStringAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture) ?? string.Empty;
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
