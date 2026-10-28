using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QuickNotes.Application;

namespace QuickNotes.Infrastructure.Sqlite;

public sealed class SqliteSnapshotProvider : IBackupSnapshotProvider
{
    private const int BusyTimeoutMilliseconds = 250;

    private readonly string _sourceDatabasePath;
    private readonly ILogger<SqliteSnapshotProvider> _logger;

    public SqliteSnapshotProvider(
        string sourceDatabasePath,
        ILogger<SqliteSnapshotProvider>? logger = null)
    {
        if (string.IsNullOrWhiteSpace(sourceDatabasePath))
        {
            throw new ArgumentException(
                "Source database path must not be empty.",
                nameof(sourceDatabasePath));
        }

        _sourceDatabasePath = sourceDatabasePath;
        _logger = logger ?? NullLogger<SqliteSnapshotProvider>.Instance;
    }

    public async Task<SnapshotInfo> CreateAsync(
        string destinationDatabasePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(destinationDatabasePath))
        {
            throw new ArgumentException(
                "Destination database path must not be empty.",
                nameof(destinationDatabasePath));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (PathsEqual(_sourceDatabasePath, destinationDatabasePath))
        {
            throw new BackupException(
                "snapshot",
                BackupErrorCodes.SnapshotFailed,
                "Source and destination database paths must differ.");
        }

        if (!File.Exists(_sourceDatabasePath))
        {
            throw new BackupException(
                "snapshot",
                BackupErrorCodes.SourceUnavailable,
                "The source database does not exist.");
        }

        if (DestinationExists(destinationDatabasePath))
        {
            throw new BackupException(
                "snapshot",
                BackupErrorCodes.SnapshotFailed,
                "The destination database already exists.");
        }

        var destinationCreated = false;
        try
        {
            await using var source = await OpenSourceAsync(cancellationToken);
            destinationCreated = true;
            await using var destination = await OpenDestinationAsync(destinationDatabasePath, cancellationToken);
            try
            {
                await Task.Run(() => source.BackupDatabase(destination), CancellationToken.None);
            }
            catch (SqliteException exception)
            {
                throw TranslateBackupFailure(exception);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var schemaVersion = await VerifySnapshotAsync(destinationDatabasePath, cancellationToken);
            return new SnapshotInfo(schemaVersion);
        }
        catch (OperationCanceledException)
        {
            if (destinationCreated)
            {
                DeleteSnapshotFiles(destinationDatabasePath);
            }

            throw;
        }
        catch (BackupException)
        {
            if (destinationCreated)
            {
                DeleteSnapshotFiles(destinationDatabasePath);
            }

            throw;
        }
        catch (IOException exception)
        {
            if (destinationCreated)
            {
                DeleteSnapshotFiles(destinationDatabasePath);
            }

            throw new BackupException(
                "snapshot",
                BackupErrorCodes.SnapshotFailed,
                "The snapshot could not be written due to an I/O error.",
                exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            if (destinationCreated)
            {
                DeleteSnapshotFiles(destinationDatabasePath);
            }

            throw new BackupException(
                "snapshot",
                BackupErrorCodes.SnapshotFailed,
                "The snapshot could not be written due to an access error.",
                exception);
        }
    }

    private async Task<SqliteConnection> OpenSourceAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await OpenConnectionAsync(
                _sourceDatabasePath,
                SqliteOpenMode.ReadOnly,
                cancellationToken);
        }
        catch (SqliteException exception)
        {
            throw new BackupException(
                "snapshot",
                BackupErrorCodes.SourceUnavailable,
                FormatSqliteError("source", exception),
                exception,
                IsBusy(exception));
        }
    }

    private static async Task<SqliteConnection> OpenDestinationAsync(
        string destinationDatabasePath,
        CancellationToken cancellationToken)
    {
        try
        {
            return await OpenConnectionAsync(
                destinationDatabasePath,
                SqliteOpenMode.ReadWriteCreate,
                cancellationToken);
        }
        catch (SqliteException exception)
        {
            throw new BackupException(
                "snapshot",
                BackupErrorCodes.SnapshotFailed,
                FormatSqliteError("destination", exception),
                exception,
                IsBusy(exception));
        }
    }

    private static async Task<SqliteConnection> OpenConnectionAsync(
        string databasePath,
        SqliteOpenMode mode,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = mode,
            Pooling = false,
            DefaultTimeout = 1
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA busy_timeout={BusyTimeoutMilliseconds};";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task<long> VerifySnapshotAsync(
        string snapshotPath,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await OpenConnectionAsync(
                snapshotPath,
                SqliteOpenMode.ReadOnly,
                cancellationToken);

            var integrity = await ScalarStringAsync(connection, "PRAGMA integrity_check;", cancellationToken);
            if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new BackupException(
                    "verify_snapshot",
                    BackupErrorCodes.SnapshotVerificationFailed,
                    "The snapshot failed integrity_check.");
            }

            if (await HasRowsAsync(connection, "PRAGMA foreign_key_check;", cancellationToken))
            {
                throw new BackupException(
                    "verify_snapshot",
                    BackupErrorCodes.SnapshotVerificationFailed,
                    "The snapshot contains foreign key violations.");
            }

            var schemaVersion = await ScalarLongAsync(connection, "PRAGMA user_version;", cancellationToken);
            if (schemaVersion < 1)
            {
                throw new BackupException(
                    "verify_snapshot",
                    BackupErrorCodes.SnapshotVerificationFailed,
                    $"The snapshot has unsupported schema version {schemaVersion}.");
            }

            return schemaVersion;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (BackupException)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            throw new BackupException(
                "verify_snapshot",
                BackupErrorCodes.SnapshotVerificationFailed,
                "The snapshot could not be verified.",
                exception);
        }
    }

    private static BackupException TranslateBackupFailure(SqliteException exception) =>
        new(
            "snapshot",
            IsBusy(exception) ? BackupErrorCodes.SourceUnavailable : BackupErrorCodes.SnapshotFailed,
            FormatSqliteError("snapshot copy", exception),
            exception,
            IsBusy(exception));

    private static bool IsBusy(SqliteException exception)
    {
        var extended = exception.SqliteExtendedErrorCode;
        var primary = extended != 0 ? extended & 0xFF : exception.SqliteErrorCode;
        return primary is 5 or 6;
    }

    private static string FormatSqliteError(string operation, SqliteException exception)
    {
        var extended = exception.SqliteExtendedErrorCode;
        var primary = extended != 0 ? extended & 0xFF : exception.SqliteErrorCode;
        return $"SQLite error {primary} (extended {extended}) during {operation}.";
    }

    private static async Task<string> ScalarStringAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static async Task<long> ScalarLongAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    private static async Task<bool> HasRowsAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);

    private static bool DestinationExists(string destinationDatabasePath) =>
        File.Exists(destinationDatabasePath)
        || File.Exists(destinationDatabasePath + "-journal")
        || File.Exists(destinationDatabasePath + "-wal")
        || File.Exists(destinationDatabasePath + "-shm");

    private void DeleteSnapshotFiles(string snapshotPath)
    {
        TryDelete(snapshotPath);
        TryDelete(snapshotPath + "-journal");
        TryDelete(snapshotPath + "-wal");
        TryDelete(snapshotPath + "-shm");
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                "Temporary snapshot file {FileName} could not be removed: {ExceptionType}.",
                Path.GetFileName(path),
                exception.GetType().Name);
        }
    }
}
