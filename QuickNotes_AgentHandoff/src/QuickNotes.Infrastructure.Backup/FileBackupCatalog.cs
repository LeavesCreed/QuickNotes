using System.Globalization;
using QuickNotes.Application;

namespace QuickNotes.Infrastructure.Backup;

public sealed class FileBackupCatalog : IBackupCatalog
{
    private readonly BackupPackageVerifier _verifier;

    public FileBackupCatalog(BackupPackageVerifier? verifier = null)
    {
        _verifier = verifier ?? new BackupPackageVerifier();
    }

    public Task<BackupCatalog> ReadForSchedulingAsync(
        string backupDirectory,
        CancellationToken cancellationToken = default) =>
        ReadAsync(backupDirectory, verifyDatabaseHashes: false, cancellationToken);

    public Task<BackupCatalog> ReadForRetentionAsync(
        string backupDirectory,
        CancellationToken cancellationToken = default) =>
        ReadAsync(backupDirectory, verifyDatabaseHashes: true, cancellationToken);

    private async Task<BackupCatalog> ReadAsync(
        string backupDirectory,
        bool verifyDatabaseHashes,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(backupDirectory) || !Directory.Exists(backupDirectory))
        {
            throw new BackupException(
                "catalog",
                BackupErrorCodes.DestinationUnavailable,
                "The backup directory is unavailable.");
        }

        List<string> paths;
        try
        {
            paths = Directory
                .EnumerateFiles(backupDirectory, "quicknotes-*.zip", SearchOption.TopDirectoryOnly)
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new BackupException(
                "catalog",
                BackupErrorCodes.DestinationUnavailable,
                "The backup directory could not be read.",
                exception);
        }

        var validEntries = new List<BackupCatalogEntry>();
        var issues = new List<BackupCatalogIssue>();
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var manifest = await _verifier.InspectAsync(
                    path,
                    verifyDatabaseHashes,
                    cancellationToken);
                var localDateTime = manifest.CreatedAtLocal.DateTime;
                validEntries.Add(new BackupCatalogEntry
                {
                    PackagePath = path,
                    Manifest = manifest,
                    LocalDate = DateOnly.FromDateTime(localDateTime),
                    IsoWeekYear = ISOWeek.GetYear(localDateTime),
                    IsoWeek = ISOWeek.GetWeekOfYear(localDateTime)
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (BackupException exception)
            {
                issues.Add(new BackupCatalogIssue(path, exception.ErrorCode, exception.Message));
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException or InvalidDataException)
            {
                issues.Add(new BackupCatalogIssue(
                    path,
                    "catalog_read_failed",
                    $"The backup package could not be read ({exception.GetType().Name})."));
            }
        }

        validEntries.Sort((left, right) =>
        {
            var byTime = right.Manifest.CreatedAtUtc.CompareTo(left.Manifest.CreatedAtUtc);
            return byTime != 0
                ? byTime
                : string.CompareOrdinal(left.PackagePath, right.PackagePath);
        });
        return new BackupCatalog
        {
            ValidEntries = validEntries,
            Issues = issues
        };
    }
}
