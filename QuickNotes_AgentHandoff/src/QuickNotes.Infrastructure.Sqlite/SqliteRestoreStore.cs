using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using QuickNotes.Application;

namespace QuickNotes.Infrastructure.Sqlite;

public sealed class SqliteRestoreStore : IRestoreStore
{
    private const int BusyTimeoutMilliseconds = 250;

    public async Task<RestoreDatabaseInfo> InspectAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException(
                "Database path must not be empty.",
                nameof(databasePath));
        }

        if (!File.Exists(databasePath))
        {
            throw new RestoreException(
                "inspect",
                RestoreErrorCodes.CurrentDatabaseUnavailable,
                "The database does not exist.");
        }

        return await InspectCoreAsync(databasePath, readWrite: false, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<string?> TryGetDatabaseHashAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException(
                "Database path must not be empty.",
                nameof(databasePath));
        }

        try
        {
            await using var stream = new FileStream(
                databasePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.Asynchronous);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken)
                .ConfigureAwait(false);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task<RestoreDatabaseInfo> PrepareCurrentAsync(
        string currentDatabasePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(currentDatabasePath))
        {
            throw new ArgumentException(
                "Current database path must not be empty.",
                nameof(currentDatabasePath));
        }

        if (!File.Exists(currentDatabasePath))
        {
            throw new RestoreException(
                "prepare",
                RestoreErrorCodes.CurrentDatabaseUnavailable,
                "The current database does not exist.");
        }

        if (File.Exists(currentDatabasePath + "-wal")
            || File.Exists(currentDatabasePath + "-shm"))
        {
            throw new RestoreException(
                "prepare",
                RestoreErrorCodes.WalUnsupported,
                "WAL sidecar files are not supported by this restore path.");
        }

        var info = await InspectCoreAsync(
                currentDatabasePath,
                readWrite: true,
                cancellationToken)
            .ConfigureAwait(false);
        if (File.Exists(currentDatabasePath + "-journal"))
        {
            throw new RestoreException(
                "prepare",
                RestoreErrorCodes.PrepareFailed,
                "The current database journal could not be recovered.");
        }

        return info;
    }

    public Task SwitchAsync(
        string stagedDatabasePath,
        string currentDatabasePath,
        string rollbackDatabasePath,
        CancellationToken cancellationToken = default)
    {
        ValidateSwitchPaths(stagedDatabasePath, currentDatabasePath, rollbackDatabasePath);
        if (File.Exists(currentDatabasePath + "-journal")
            || File.Exists(currentDatabasePath + "-wal")
            || File.Exists(currentDatabasePath + "-shm"))
        {
            throw new RestoreException(
                "switch",
                RestoreErrorCodes.PrepareFailed,
                "The current database has sidecar files; the switch was aborted.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (File.Exists(rollbackDatabasePath))
            {
                File.Delete(rollbackDatabasePath);
            }

            File.Replace(
                stagedDatabasePath,
                currentDatabasePath,
                rollbackDatabasePath,
                ignoreMetadataErrors: true);
            return Task.CompletedTask;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new RestoreException(
                "switch",
                RestoreErrorCodes.SwitchFailed,
                "The restored database could not be switched in.",
                exception);
        }
    }

    public Task RollbackAsync(
        string rollbackDatabasePath,
        string currentDatabasePath,
        string failedDatabasePath,
        CancellationToken cancellationToken = default)
    {
        ValidateRollbackPaths(rollbackDatabasePath, currentDatabasePath);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (File.Exists(failedDatabasePath))
            {
                File.Delete(failedDatabasePath);
            }

            File.Replace(
                rollbackDatabasePath,
                currentDatabasePath,
                failedDatabasePath,
                ignoreMetadataErrors: true);
            return Task.CompletedTask;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new RestoreException(
                "rollback",
                RestoreErrorCodes.RollbackFailed,
                "The previous database could not be rolled back.",
                exception);
        }
    }

    private static async Task<RestoreDatabaseInfo> InspectCoreAsync(
        string databasePath,
        bool readWrite,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = readWrite ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly,
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

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA integrity_check;";
                var integrity = Convert.ToString(
                    await command.ExecuteScalarAsync(cancellationToken),
                    CultureInfo.InvariantCulture);
                if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new RestoreException(
                        "inspect",
                        RestoreErrorCodes.InspectFailed,
                        "The database failed integrity_check.");
                }
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA foreign_key_check;";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    throw new RestoreException(
                        "inspect",
                        RestoreErrorCodes.InspectFailed,
                        "The database contains foreign key violations.");
                }
            }

            long schemaVersion;
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA user_version;";
                schemaVersion = Convert.ToInt64(
                    await command.ExecuteScalarAsync(cancellationToken),
                    CultureInfo.InvariantCulture);
            }

            RestoreCounts counts;
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT
                        (SELECT COUNT(*) FROM notes WHERE lifecycle = 0),
                        (SELECT COUNT(*) FROM notes WHERE lifecycle = 1),
                        (SELECT COUNT(*) FROM notes WHERE lifecycle = 2),
                        (SELECT COUNT(*) FROM categories),
                        (SELECT COUNT(*) FROM tags),
                        (SELECT COUNT(*) FROM note_tags);
                    """;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    throw new RestoreException(
                        "inspect",
                        RestoreErrorCodes.InspectFailed,
                        "The database counts could not be read.");
                }

                counts = new RestoreCounts(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    reader.GetInt64(5));
            }

            return new RestoreDatabaseInfo(schemaVersion, counts);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RestoreException)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            throw new RestoreException(
                "inspect",
                RestoreErrorCodes.InspectFailed,
                "The database could not be inspected.",
                exception);
        }
    }

    private static void ValidateRollbackPaths(
        string rollbackDatabasePath,
        string currentDatabasePath)
    {
        if (string.IsNullOrWhiteSpace(rollbackDatabasePath)
            || string.IsNullOrWhiteSpace(currentDatabasePath))
        {
            throw new ArgumentException("Restore rollback paths must not be empty.");
        }

        if (!File.Exists(rollbackDatabasePath))
        {
            throw new RestoreException(
                "rollback",
                RestoreErrorCodes.RollbackFailed,
                "The rollback database does not exist.");
        }

        if (!File.Exists(currentDatabasePath))
        {
            throw new RestoreException(
                "rollback",
                RestoreErrorCodes.CurrentDatabaseUnavailable,
                "The current database does not exist.");
        }
    }

    private static void ValidateSwitchPaths(
        string sourcePath,
        string currentDatabasePath,
        string rollbackDatabasePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)
            || string.IsNullOrWhiteSpace(currentDatabasePath)
            || string.IsNullOrWhiteSpace(rollbackDatabasePath))
        {
            throw new ArgumentException("Restore switch paths must not be empty.");
        }

        if (!File.Exists(sourcePath))
        {
            throw new RestoreException(
                "switch",
                RestoreErrorCodes.SwitchFailed,
                "The staged or rollback database does not exist.");
        }

        if (!File.Exists(currentDatabasePath))
        {
            throw new RestoreException(
                "switch",
                RestoreErrorCodes.CurrentDatabaseUnavailable,
                "The current database does not exist.");
        }
    }
}
