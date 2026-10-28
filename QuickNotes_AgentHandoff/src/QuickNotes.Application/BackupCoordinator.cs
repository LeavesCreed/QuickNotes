using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace QuickNotes.Application;

public sealed class BackupCoordinator : IBackupCoordinator
{
    private readonly IBackupSnapshotProvider _snapshots;
    private readonly IBackupPackageStore _packages;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BackupCoordinator> _logger;

    public BackupCoordinator(
        IBackupSnapshotProvider snapshots,
        IBackupPackageStore packages,
        TimeProvider? timeProvider = null,
        ILogger<BackupCoordinator>? logger = null)
    {
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _packages = packages ?? throw new ArgumentNullException(nameof(packages));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<BackupCoordinator>.Instance;
    }

    public async Task<BackupResult> CreateAsync(
        BackupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var validation = ValidateRequest(request);
        if (validation is not null)
        {
            return validation;
        }

        var startedAt = Stopwatch.GetTimestamp();
        var backupId = Guid.NewGuid();
        var workingDirectory = Path.Combine(
            request.BackupDirectory,
            $".quicknotes-tmp-{backupId:N}");
        try
        {
            try
            {
                Directory.CreateDirectory(workingDirectory);
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                _logger.LogWarning(
                    "Backup working directory could not be created: {ExceptionType}.",
                    exception.GetType().Name);
                return Failure(
                    BackupStatus.StorageUnavailable,
                    BackupErrorCode.DestinationUnavailable,
                    "The backup directory is not writable.",
                    isRetryable: true,
                    diagnostics: "destination_unavailable",
                    durationMilliseconds: ElapsedMilliseconds(startedAt));
            }

            var snapshotPath = Path.Combine(workingDirectory, "snapshot.db");
            var snapshot = await _snapshots.CreateAsync(snapshotPath, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var utcNow = _timeProvider.GetUtcNow().ToUniversalTime();
            var localNow = TimeZoneInfo.ConvertTime(utcNow, _timeProvider.LocalTimeZone);
            var package = await _packages.PublishAsync(
                    snapshotPath,
                    snapshot.SchemaVersion,
                    request,
                    utcNow,
                    localNow,
                    cancellationToken)
                .ConfigureAwait(false);

            var duration = ElapsedMilliseconds(startedAt);
            _logger.LogInformation(
                "Backup {BackupType} ({BackupId}) succeeded in {DurationMs} ms with package {PackageFileName}.",
                request.Type,
                backupId,
                duration,
                Path.GetFileName(package.PackagePath));
            return new BackupResult
            {
                Status = BackupStatus.Succeeded,
                PackagePath = package.PackagePath,
                Manifest = package.Manifest,
                DurationMilliseconds = duration
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (BackupException exception)
        {
            _logger.LogWarning(
                "Backup {BackupType} ({BackupId}) failed with {ErrorCode} at {Operation} ({ExceptionType}, inner {InnerExceptionType}).",
                request.Type,
                backupId,
                exception.ErrorCode,
                exception.Operation,
                exception.GetType().Name,
                exception.InnerException?.GetType().Name ?? "none");
            return MapFailure(exception, backupId, startedAt);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Unexpected error during backup {BackupType}.",
                request.Type);
            throw;
        }
        finally
        {
            CleanUpWorkingDirectory(workingDirectory);
        }
    }

    private static BackupResult? ValidateRequest(BackupRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.BackupDirectory))
        {
            return Failure(
                BackupStatus.ValidationFailed,
                BackupErrorCode.ValidationFailed,
                "Backup directory must not be empty.",
                isRetryable: false,
                diagnostics: "backup_directory_empty",
                durationMilliseconds: 0);
        }

        if (request.Type != BackupType.Manual)
        {
            return Failure(
                BackupStatus.ValidationFailed,
                BackupErrorCode.ValidationFailed,
                "Only manual backups are supported.",
                isRetryable: false,
                diagnostics: "unsupported_backup_type",
                durationMilliseconds: 0);
        }

        if (!Directory.Exists(request.BackupDirectory))
        {
            return Failure(
                BackupStatus.StorageUnavailable,
                BackupErrorCode.DestinationUnavailable,
                "The backup directory does not exist.",
                isRetryable: true,
                diagnostics: "destination_not_found",
                durationMilliseconds: 0);
        }

        return null;
    }

    private static BackupResult MapFailure(
        BackupException exception,
        Guid backupId,
        long startedAt)
    {
        var (status, code, isRetryable) = exception.ErrorCode switch
        {
            BackupErrorCodes.SourceUnavailable =>
                (BackupStatus.SnapshotFailed, BackupErrorCode.SourceUnavailable, exception.IsRetryable),
            BackupErrorCodes.SnapshotFailed =>
                (BackupStatus.SnapshotFailed, BackupErrorCode.SnapshotFailed, exception.IsRetryable),
            BackupErrorCodes.SnapshotVerificationFailed =>
                (BackupStatus.VerificationFailed, BackupErrorCode.SnapshotVerificationFailed, false),
            BackupErrorCodes.PackageWriteFailed =>
                (BackupStatus.StorageUnavailable, BackupErrorCode.PackageWriteFailed, exception.IsRetryable),
            BackupErrorCodes.DestinationUnavailable =>
                (BackupStatus.StorageUnavailable, BackupErrorCode.DestinationUnavailable, true),
            BackupErrorCodes.PackageVerificationFailed or BackupErrorCodes.ManifestInvalid =>
                (BackupStatus.VerificationFailed, BackupErrorCode.PackageVerificationFailed, false),
            _ => (BackupStatus.StorageUnavailable, BackupErrorCode.PackageWriteFailed, false)
        };
        return Failure(
            status,
            code,
            $"Backup failed: {exception.ErrorCode}.",
            isRetryable,
            $"backup_id={backupId:N}; operation={exception.Operation}; error={exception.ErrorCode}; detail={exception.Message}",
            ElapsedMilliseconds(startedAt));
    }

    private static BackupResult Failure(
        BackupStatus status,
        BackupErrorCode code,
        string message,
        bool isRetryable,
        string diagnostics,
        long durationMilliseconds) => new()
    {
        Status = status,
        Error = new BackupError(code, message, isRetryable, diagnostics),
        DurationMilliseconds = durationMilliseconds
    };

    private void CleanUpWorkingDirectory(string workingDirectory)
    {
        try
        {
            if (Directory.Exists(workingDirectory))
            {
                Directory.Delete(workingDirectory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                "Backup working directory {WorkingDirectoryName} could not be removed: {ExceptionType}.",
                Path.GetFileName(workingDirectory),
                exception.GetType().Name);
        }
    }

    private static long ElapsedMilliseconds(long startedAt) =>
        (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
}
