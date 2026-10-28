using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QuickNotes.Application;

namespace QuickNotes.Infrastructure.Backup;

public sealed class BackupPackageVerifier
{
    public const long DefaultMaxDatabaseBytes = 4L * 1024 * 1024 * 1024;

    private const int BufferSize = 81920;
    private const int MaxManifestBytes = 1024 * 1024;
    private const int BusyTimeoutMilliseconds = 250;

    private readonly long _maxDatabaseBytes;
    private readonly ILogger<BackupPackageVerifier> _logger;

    public BackupPackageVerifier(
        long maxDatabaseBytes = DefaultMaxDatabaseBytes,
        ILogger<BackupPackageVerifier>? logger = null)
    {
        if (maxDatabaseBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxDatabaseBytes),
                maxDatabaseBytes,
                "Maximum database size must be positive.");
        }

        _maxDatabaseBytes = maxDatabaseBytes;
        _logger = logger ?? NullLogger<BackupPackageVerifier>.Instance;
    }

    public async Task<BackupManifest> VerifyAsync(
        string packagePath,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            throw new ArgumentException("Package path must not be empty.", nameof(packagePath));
        }

        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            throw new ArgumentException("Working directory must not be empty.", nameof(workingDirectory));
        }

        if (!File.Exists(packagePath))
        {
            throw Failure("The backup package does not exist.");
        }

        if (!Directory.Exists(workingDirectory))
        {
            throw Failure("The verification directory does not exist.");
        }

        var verificationPath = Path.Combine(
            workingDirectory,
            $".quicknotes-verify-{Guid.NewGuid():N}.db");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var archive = OpenArchive(packagePath);
            var (manifestEntry, databaseEntry) = FindEntries(archive);
            var manifest = await ReadManifestAsync(manifestEntry, cancellationToken);
            if (manifest.DatabaseSizeBytes > _maxDatabaseBytes)
            {
                throw Failure("The packaged database exceeds the verification size limit.");
            }

            var (size, sha256) = await ExtractAndHashAsync(
                databaseEntry,
                verificationPath,
                _maxDatabaseBytes,
                cancellationToken);
            if (size != manifest.DatabaseSizeBytes)
            {
                throw Failure("The database entry size does not match the manifest.");
            }

            if (!string.Equals(sha256, manifest.DatabaseSha256, StringComparison.Ordinal))
            {
                throw Failure("The database entry hash does not match the manifest.");
            }

            await VerifyDatabaseAsync(verificationPath, manifest.SchemaVersion, cancellationToken);
            return manifest;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (BackupException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            InvalidDataException or IOException or UnauthorizedAccessException or SqliteException)
        {
            throw Failure("The backup package could not be verified.", exception);
        }
        finally
        {
            TryDelete(verificationPath);
        }
    }

    public async Task<BackupManifest> InspectAsync(
        string packagePath,
        bool verifyDatabaseHash = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            throw new ArgumentException("Package path must not be empty.", nameof(packagePath));
        }

        if (!File.Exists(packagePath))
        {
            throw Failure("The backup package does not exist.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var archive = OpenArchive(packagePath);
            var (manifestEntry, databaseEntry) = FindEntries(archive);
            var manifest = await ReadManifestAsync(manifestEntry, cancellationToken);
            if (manifest.DatabaseSizeBytes > _maxDatabaseBytes)
            {
                throw Failure("The packaged database exceeds the verification size limit.");
            }

            if (!verifyDatabaseHash)
            {
                if (databaseEntry.Length != manifest.DatabaseSizeBytes)
                {
                    throw Failure("The database entry size does not match the manifest.");
                }

                return manifest;
            }

            var (size, sha256) = await HashEntryAsync(
                databaseEntry,
                _maxDatabaseBytes,
                cancellationToken);
            if (size != manifest.DatabaseSizeBytes)
            {
                throw Failure("The database entry size does not match the manifest.");
            }

            if (!string.Equals(sha256, manifest.DatabaseSha256, StringComparison.Ordinal))
            {
                throw Failure("The database entry hash does not match the manifest.");
            }

            return manifest;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (BackupException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw Failure("The backup package could not be inspected.", exception);
        }
    }

    private static ZipArchive OpenArchive(string packagePath)
    {
        try
        {
            return ZipFile.OpenRead(packagePath);
        }
        catch (Exception exception) when (exception is
            InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw Failure("The backup package is not a readable ZIP archive.", exception);
        }
    }

    private static (ZipArchiveEntry Manifest, ZipArchiveEntry Database) FindEntries(ZipArchive archive)
    {
        if (archive.Entries.Count != 2)
        {
            throw Failure("The backup package must contain exactly two entries.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (!names.Add(entry.FullName))
            {
                throw Failure("The backup package contains duplicate entries.");
            }
        }

        var manifest = archive.GetEntry(BackupManifest.ManifestEntryName);
        var database = archive.GetEntry(BackupManifest.DatabaseEntryName);
        if (manifest is null || database is null)
        {
            throw Failure("The backup package entries are invalid.");
        }

        return (manifest, database);
    }

    private static async Task<BackupManifest> ReadManifestAsync(
        ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        await using var stream = entry.Open();
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (memory.Length + read > MaxManifestBytes)
            {
                throw Failure("The backup manifest is too large.");
            }

            memory.Write(buffer, 0, read);
        }

        return BackupManifestSerializer.Deserialize(memory.ToArray());
    }

    private static async Task<(long Size, string Sha256)> ExtractAndHashAsync(
        ZipArchiveEntry entry,
        string destinationPath,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous);
        var result = await HashEntryCoreAsync(
            entry,
            output,
            maxBytes,
            cancellationToken);
        await output.FlushAsync(cancellationToken);
        return result;
    }

    private static Task<(long Size, string Sha256)> HashEntryAsync(
        ZipArchiveEntry entry,
        long maxBytes,
        CancellationToken cancellationToken) =>
        HashEntryCoreAsync(entry, destination: null, maxBytes, cancellationToken);

    private static async Task<(long Size, string Sha256)> HashEntryCoreAsync(
        ZipArchiveEntry entry,
        Stream? destination,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long size = 0;
        await using var input = entry.Open();
        var buffer = new byte[BufferSize];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (size + read > maxBytes)
            {
                throw Failure("The packaged database exceeds the verification size limit.");
            }

            hash.AppendData(buffer, 0, read);
            if (destination is not null)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            size += read;
        }

        return (size, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static async Task VerifyDatabaseAsync(
        string databasePath,
        long expectedSchemaVersion,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 1
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
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
                throw Failure("The packaged database failed integrity_check.");
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_key_check;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                throw Failure("The packaged database contains foreign key violations.");
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version;";
            var schemaVersion = Convert.ToInt64(
                await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);
            if (schemaVersion != expectedSchemaVersion)
            {
                throw Failure("The packaged database schema version does not match the manifest.");
            }
        }
    }

    private static BackupException Failure(string message, Exception? inner = null) =>
        new(
            "verify_package",
            BackupErrorCodes.PackageVerificationFailed,
            message,
            inner);

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                "Temporary verification file {FileName} could not be removed: {ExceptionType}.",
                Path.GetFileName(path),
                exception.GetType().Name);
        }
    }
}
