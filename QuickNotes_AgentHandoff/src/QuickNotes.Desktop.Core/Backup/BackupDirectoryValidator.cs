namespace QuickNotes.Desktop.Backup;

public enum BackupDirectoryValidationStatus
{
    Valid,
    Empty,
    NotAbsolute,
    SameAsDataRoot,
    Missing,
    NotWritable,
    Error
}

public sealed record BackupDirectoryValidationResult(
    BackupDirectoryValidationStatus Status,
    string? ErrorCode,
    string? Message);

public static class BackupDirectoryValidator
{
    public static async Task<BackupDirectoryValidationResult> ValidateAsync(
        string? directory,
        string dataRoot,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return new BackupDirectoryValidationResult(
                BackupDirectoryValidationStatus.Empty,
                "backup_directory_empty",
                "A backup directory must be selected.");
        }

        if (!Path.IsPathFullyQualified(directory))
        {
            return new BackupDirectoryValidationResult(
                BackupDirectoryValidationStatus.NotAbsolute,
                "backup_directory_not_absolute",
                "The backup directory must be an absolute path.");
        }

        string fullDirectory;
        string fullDataRoot;
        try
        {
            fullDirectory = Path.GetFullPath(directory);
            fullDataRoot = Path.GetFullPath(dataRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new BackupDirectoryValidationResult(
                BackupDirectoryValidationStatus.Error,
                "backup_directory_invalid",
                exception.GetType().Name);
        }

        if (IsSameOrNested(fullDirectory, fullDataRoot)
            || IsSameOrNested(fullDataRoot, fullDirectory))
        {
            return new BackupDirectoryValidationResult(
                BackupDirectoryValidationStatus.SameAsDataRoot,
                "backup_directory_overlaps_data_root",
                "The backup directory must be separate from the application data directory.");
        }

        if (!Directory.Exists(fullDirectory))
        {
            return new BackupDirectoryValidationResult(
                BackupDirectoryValidationStatus.Missing,
                "backup_directory_missing",
                "The selected backup directory does not exist.");
        }

        var probePath = Path.Combine(
            fullDirectory,
            $".quicknotes-write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(new byte[] { 1 }, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Delete(probePath);
            return new BackupDirectoryValidationResult(
                BackupDirectoryValidationStatus.Valid,
                null,
                null);
        }
        catch (OperationCanceledException)
        {
            TryDelete(probePath);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDelete(probePath);
            return new BackupDirectoryValidationResult(
                BackupDirectoryValidationStatus.NotWritable,
                "backup_directory_not_writable",
                exception.GetType().Name);
        }
    }

    private static bool IsSameOrNested(string candidate, string root)
    {
        var normalizedCandidate = TrimTrailingSeparator(Path.GetFullPath(candidate));
        var normalizedRoot = TrimTrailingSeparator(Path.GetFullPath(root));
        return string.Equals(
                normalizedCandidate,
                normalizedRoot,
                StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimTrailingSeparator(string path) =>
        path.Length > 1
            ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : path;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
