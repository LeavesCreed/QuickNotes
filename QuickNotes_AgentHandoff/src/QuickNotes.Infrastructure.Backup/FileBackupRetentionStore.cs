using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QuickNotes.Application;

namespace QuickNotes.Infrastructure.Backup;

public sealed class FileBackupRetentionStore : IBackupRetentionStore
{
    private readonly ILogger<FileBackupRetentionStore> _logger;

    public FileBackupRetentionStore(ILogger<FileBackupRetentionStore>? logger = null)
    {
        _logger = logger ?? NullLogger<FileBackupRetentionStore>.Instance;
    }

    public Task<BackupRetentionResult> DeleteAsync(
        string backupDirectory,
        IReadOnlyList<BackupCatalogEntry> packages,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(backupDirectory))
        {
            throw new ArgumentException(
                "Backup directory must not be empty.",
                nameof(backupDirectory));
        }

        ArgumentNullException.ThrowIfNull(packages);
        if (!Directory.Exists(backupDirectory))
        {
            return Task.FromResult(new BackupRetentionResult(
                Array.Empty<string>(),
                new[]
                {
                    new BackupRetentionWarning(
                        backupDirectory,
                        "retention_directory_unavailable",
                        "The backup directory is unavailable.")
                }));
        }

        var deleted = new List<string>();
        var warnings = new List<BackupRetentionWarning>();
        var root = Path.GetFullPath(backupDirectory);
        foreach (var package in packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (package.Manifest.BackupType != BackupType.Daily)
            {
                warnings.Add(new BackupRetentionWarning(
                    package.PackagePath,
                    "retention_unsafe_type",
                    "Only automatic daily backups may be cleaned up during retention."));
                continue;
            }

            var fullPath = Path.GetFullPath(package.PackagePath);
            if (!IsInside(root, fullPath)
                || !string.Equals(Path.GetExtension(fullPath), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new BackupRetentionWarning(
                    package.PackagePath,
                    "retention_unsafe_path",
                    "The package path is outside the backup directory or is not a ZIP file."));
                continue;
            }

            try
            {
                File.Delete(fullPath);
                deleted.Add(package.PackagePath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                warnings.Add(new BackupRetentionWarning(
                    package.PackagePath,
                    "retention_delete_failed",
                    $"The package could not be deleted: {exception.GetType().Name}."));
                _logger.LogWarning(
                    "Backup package {FileName} could not be deleted during retention: {ExceptionType}.",
                    Path.GetFileName(fullPath),
                    exception.GetType().Name);
            }
        }

        return Task.FromResult(new BackupRetentionResult(deleted, warnings));
    }

    private static bool IsInside(string root, string path)
    {
        var normalizedRoot = root.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var rootWithSeparator = normalizedRoot + Path.DirectorySeparatorChar;
        return path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(path, normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}
