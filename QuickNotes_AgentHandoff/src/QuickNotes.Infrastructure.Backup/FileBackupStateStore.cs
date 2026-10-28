using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QuickNotes.Application;

namespace QuickNotes.Infrastructure.Backup;

public sealed class FileBackupStateStore : IBackupStateStore
{
    public const string StateFileName = ".quicknotes-backup-state.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly ILogger<FileBackupStateStore> _logger;

    public FileBackupStateStore(ILogger<FileBackupStateStore>? logger = null)
    {
        _logger = logger ?? NullLogger<FileBackupStateStore>.Instance;
    }

    public async Task<BackupScheduleState?> ReadAsync(
        string backupDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(backupDirectory))
        {
            throw new ArgumentException(
                "Backup directory must not be empty.",
                nameof(backupDirectory));
        }

        var path = GetStatePath(backupDirectory);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            var state = JsonSerializer.Deserialize<BackupScheduleState>(bytes, SerializerOptions);
            if (!IsValid(state))
            {
                _logger.LogWarning(
                    "Backup schedule state is invalid; treating it as absent.");
                return null;
            }

            return state;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException)
        {
            _logger.LogWarning(
                "Backup schedule state is not valid JSON; treating it as absent.");
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                "Backup schedule state could not be read: {ExceptionType}.",
                exception.GetType().Name);
            return null;
        }
    }

    public async Task WriteAsync(
        string backupDirectory,
        BackupScheduleState state,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(backupDirectory))
        {
            throw new ArgumentException(
                "Backup directory must not be empty.",
                nameof(backupDirectory));
        }

        ArgumentNullException.ThrowIfNull(state);
        if (!IsValid(state))
        {
            throw new ArgumentException("Backup schedule state is invalid.", nameof(state));
        }

        if (!Directory.Exists(backupDirectory))
        {
            throw new BackupException(
                "state",
                BackupErrorCodes.DestinationUnavailable,
                "The backup directory is unavailable.");
        }

        var targetPath = GetStatePath(backupDirectory);
        var temporaryPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(state, SerializerOptions);
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            TryDelete(temporaryPath);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporaryPath);
            throw new BackupException(
                "state",
                BackupErrorCodes.DestinationUnavailable,
                "The backup schedule state could not be written.",
                exception);
        }
    }

    private static bool IsValid(BackupScheduleState? state) =>
        state is not null
        && state.FormatVersion == BackupScheduleState.CurrentFormatVersion
        && !string.IsNullOrWhiteSpace(state.LastSuccessfulFingerprint)
        && state.LastSuccessfulDailyAtUtc != default
        && state.LastSuccessfulDailyLocalDate != default;

    private static string GetStatePath(string backupDirectory) =>
        Path.Combine(backupDirectory, StateFileName);

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                "Temporary backup state file {FileName} could not be removed: {ExceptionType}.",
                Path.GetFileName(path),
                exception.GetType().Name);
        }
    }
}
