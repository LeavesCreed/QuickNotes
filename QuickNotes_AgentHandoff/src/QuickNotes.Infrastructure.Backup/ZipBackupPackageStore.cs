using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QuickNotes.Application;

namespace QuickNotes.Infrastructure.Backup;

public sealed class ZipBackupPackageStore : IBackupPackageStore
{
    private const int BufferSize = 81920;

    private readonly BackupPackageVerifier _verifier;
    private readonly ILogger<ZipBackupPackageStore> _logger;

    public ZipBackupPackageStore(
        BackupPackageVerifier? verifier = null,
        ILogger<ZipBackupPackageStore>? logger = null)
    {
        _verifier = verifier ?? new BackupPackageVerifier();
        _logger = logger ?? NullLogger<ZipBackupPackageStore>.Instance;
    }

    public async Task<BackupPackageInfo> PublishAsync(
        string snapshotDatabasePath,
        long schemaVersion,
        BackupRequest request,
        DateTimeOffset createdAtUtc,
        DateTimeOffset createdAtLocal,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(snapshotDatabasePath))
        {
            throw new ArgumentException(
                "Snapshot database path must not be empty.",
                nameof(snapshotDatabasePath));
        }

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.BackupDirectory) || !Directory.Exists(request.BackupDirectory))
        {
            throw new BackupException(
                "package",
                BackupErrorCodes.DestinationUnavailable,
                "The backup directory is unavailable.");
        }

        if (!File.Exists(snapshotDatabasePath))
        {
            throw new BackupException(
                "package",
                BackupErrorCodes.PackageWriteFailed,
                "The snapshot database does not exist.");
        }

        if (!Enum.IsDefined(request.Type))
        {
            throw new ArgumentException("Backup type must be defined.", nameof(request));
        }

        if (createdAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("CreatedAtUtc must use UTC.", nameof(createdAtUtc));
        }

        if (createdAtUtc == default
            || createdAtLocal == default
            || createdAtUtc.UtcDateTime != createdAtLocal.UtcDateTime)
        {
            throw new ArgumentException(
                "Backup timestamps must represent the same instant.",
                nameof(createdAtUtc));
        }

        if (schemaVersion < 1)
        {
            throw new ArgumentException("SchemaVersion must be at least 1.", nameof(schemaVersion));
        }

        cancellationToken.ThrowIfCancellationRequested();

        long size;
        string sha256;
        try
        {
            (size, sha256) = await ComputeFileHashAsync(snapshotDatabasePath, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new BackupException(
                "package",
                BackupErrorCodes.PackageWriteFailed,
                "The snapshot database could not be read.",
                exception);
        }

        var manifest = new BackupManifest
        {
            FormatVersion = BackupManifest.CurrentFormatVersion,
            BackupType = request.Type,
            CreatedAtUtc = createdAtUtc,
            CreatedAtLocal = createdAtLocal,
            SchemaVersion = schemaVersion,
            DatabaseFileName = BackupManifest.DatabaseEntryName,
            DatabaseSizeBytes = size,
            DatabaseSha256 = sha256
        };
        BackupManifestSerializer.Validate(manifest);

        var tempPackagePath = Path.Combine(
            request.BackupDirectory,
            $".quicknotes-package-{Guid.NewGuid():N}.tmp");
        try
        {
            try
            {
                await WritePackageAsync(
                    tempPackagePath,
                    snapshotDatabasePath,
                    manifest,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException or InvalidDataException)
            {
                throw new BackupException(
                    "package",
                    BackupErrorCodes.PackageWriteFailed,
                    "The backup package could not be written.",
                    exception);
            }

            var verifiedManifest = await _verifier.VerifyAsync(
                tempPackagePath,
                request.BackupDirectory,
                cancellationToken);
            if (verifiedManifest != manifest)
            {
                throw new BackupException(
                    "verify_package",
                    BackupErrorCodes.PackageVerificationFailed,
                    "The verified manifest does not match the written package.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var finalPath = PublishAtomically(
                tempPackagePath,
                request.BackupDirectory,
                createdAtUtc,
                request.Type);
            return new BackupPackageInfo(finalPath, verifiedManifest);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (BackupException)
        {
            throw;
        }
        finally
        {
            TryDelete(tempPackagePath);
        }
    }

    private static async Task WritePackageAsync(
        string packagePath,
        string snapshotDatabasePath,
        BackupManifest manifest,
        CancellationToken cancellationToken)
    {
        await using var packageStream = new FileStream(
            packagePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous);
        using var archive = new ZipArchive(packageStream, ZipArchiveMode.Create, leaveOpen: true);

        var manifestEntry = archive.CreateEntry(
            BackupManifest.ManifestEntryName,
            CompressionLevel.Optimal);
        var manifestBytes = BackupManifestSerializer.Serialize(manifest);
        await using (var entryStream = manifestEntry.Open())
        {
            await entryStream.WriteAsync(manifestBytes, cancellationToken);
        }

        var databaseEntry = archive.CreateEntry(
            BackupManifest.DatabaseEntryName,
            CompressionLevel.Optimal);
        await using (var entryStream = databaseEntry.Open())
        await using (var snapshotStream = new FileStream(
            snapshotDatabasePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous))
        {
            await snapshotStream.CopyToAsync(entryStream, cancellationToken);
        }
    }

    private static async Task<(long Size, string Sha256)> ComputeFileHashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous);
        var size = stream.Length;
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return (size, Convert.ToHexString(hash).ToLowerInvariant());
    }

    private static string PublishAtomically(
        string tempPackagePath,
        string backupDirectory,
        DateTimeOffset createdAtUtc,
        BackupType backupType)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var finalPath = BuildFinalPath(backupDirectory, createdAtUtc, backupType, attempt);
            if (File.Exists(finalPath))
            {
                continue;
            }

            try
            {
                File.Move(tempPackagePath, finalPath);
                return finalPath;
            }
            catch (IOException) when (File.Exists(finalPath))
            {
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new BackupException(
                    "publish",
                    BackupErrorCodes.PackageWriteFailed,
                    "The backup package could not be published.",
                    exception);
            }
        }

        throw new BackupException(
            "publish",
            BackupErrorCodes.PackageWriteFailed,
            "A unique backup package name could not be allocated.");
    }

    private static string BuildFinalPath(
        string backupDirectory,
        DateTimeOffset createdAtUtc,
        BackupType backupType,
        int attempt)
    {
        var timestamp = createdAtUtc.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var suffix = attempt == 0
            ? string.Empty
            : $"-{Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLowerInvariant()}";
        var type = backupType.ToString().ToLowerInvariant();
        return Path.Combine(backupDirectory, $"quicknotes-{timestamp}-{type}{suffix}.zip");
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
                "Temporary package file {FileName} could not be removed: {ExceptionType}.",
                Path.GetFileName(path),
                exception.GetType().Name);
        }
    }
}
