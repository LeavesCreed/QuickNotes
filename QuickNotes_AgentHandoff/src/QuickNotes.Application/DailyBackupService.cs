using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace QuickNotes.Application;

public sealed class DailyBackupService : IDailyBackupService
{
    private readonly IBackupChangeDetector _changeDetector;
    private readonly IBackupCatalog _catalog;
    private readonly IBackupCoordinator _backups;
    private readonly IBackupRetentionPolicy _retentionPolicy;
    private readonly IBackupRetentionStore _retentionStore;
    private readonly IBackupStateStore _stateStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DailyBackupService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DailyBackupService(
        IBackupChangeDetector changeDetector,
        IBackupCatalog catalog,
        IBackupCoordinator backups,
        IBackupRetentionPolicy retentionPolicy,
        IBackupRetentionStore retentionStore,
        IBackupStateStore stateStore,
        TimeProvider? timeProvider = null,
        ILogger<DailyBackupService>? logger = null)
    {
        _changeDetector = changeDetector ?? throw new ArgumentNullException(nameof(changeDetector));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _backups = backups ?? throw new ArgumentNullException(nameof(backups));
        _retentionPolicy = retentionPolicy ?? throw new ArgumentNullException(nameof(retentionPolicy));
        _retentionStore = retentionStore ?? throw new ArgumentNullException(nameof(retentionStore));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<DailyBackupService>.Instance;
    }

    public async Task<DailyBackupResult> RunCheckAsync(
        string backupDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(backupDirectory))
        {
            throw new ArgumentException(
                "Backup directory must not be empty.",
                nameof(backupDirectory));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var startedAt = Stopwatch.GetTimestamp();
        var checkedAt = _timeProvider.GetUtcNow().ToUniversalTime();
        var localDate = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(checkedAt, _timeProvider.LocalTimeZone).DateTime);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            BackupCatalog schedulingCatalog;
            try
            {
                schedulingCatalog = await _catalog
                    .ReadForSchedulingAsync(backupDirectory, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (BackupException exception)
            {
                _logger.LogWarning(
                    "Daily backup check failed during catalog read: {ErrorCode}.",
                    exception.ErrorCode);
                return CreateFailure(checkedAt, localDate, exception, startedAt);
            }

            if (schedulingCatalog.Issues.Count > 0)
            {
                _logger.LogWarning(
                    "Backup catalog reported {IssueCount} invalid package(s) during scheduling.",
                    schedulingCatalog.Issues.Count);
            }

            var existingToday = schedulingCatalog.ValidEntries.FirstOrDefault(
                entry => entry.Manifest.BackupType == BackupType.Daily
                    && entry.LocalDate == localDate);
            if (existingToday is not null)
            {
                _logger.LogInformation(
                    "Daily backup already exists for {LocalDate}.",
                    localDate);
                return new DailyBackupResult
                {
                    Outcome = DailyBackupOutcome.AlreadyBackedUpToday,
                    CheckedAtUtc = checkedAt,
                    LocalDate = localDate,
                    ExistingDailyPackagePath = existingToday.PackagePath,
                    DurationMilliseconds = ElapsedMilliseconds(startedAt)
                };
            }

            string fingerprint;
            try
            {
                fingerprint = await _changeDetector
                    .GetFingerprintAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (BackupException exception)
            {
                _logger.LogWarning(
                    "Daily backup check failed during change detection: {ErrorCode}.",
                    exception.ErrorCode);
                return CreateFailure(checkedAt, localDate, exception, startedAt);
            }

            BackupScheduleState? state;
            try
            {
                state = await _stateStore
                    .ReadAsync(backupDirectory, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (BackupException exception)
            {
                _logger.LogWarning(
                    "Daily backup check failed while reading the schedule state: {ErrorCode}.",
                    exception.ErrorCode);
                return CreateFailure(checkedAt, localDate, exception, startedAt);
            }

            var baselineExists = state is not null
                && schedulingCatalog.ValidEntries.Any(entry =>
                    entry.Manifest.BackupType == BackupType.Daily
                    && entry.LocalDate == state.LastSuccessfulDailyLocalDate
                    && entry.Manifest.CreatedAtUtc == state.LastSuccessfulDailyAtUtc);
            if (baselineExists
                && string.Equals(
                    state!.LastSuccessfulFingerprint,
                    fingerprint,
                    StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "No data changes since the last daily backup; skipping.");
                return new DailyBackupResult
                {
                    Outcome = DailyBackupOutcome.NoChanges,
                    CheckedAtUtc = checkedAt,
                    LocalDate = localDate,
                    DurationMilliseconds = ElapsedMilliseconds(startedAt)
                };
            }

            var backup = await _backups.CreateAsync(
                    new BackupRequest
                    {
                        BackupDirectory = backupDirectory,
                        Type = BackupType.Daily
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (backup.Status != BackupStatus.Succeeded
                || backup.PackagePath is null
                || backup.Manifest is null)
            {
                _logger.LogWarning(
                    "Daily backup failed with {Status} and {ErrorCode}.",
                    backup.Status,
                    backup.Error?.Code);
                return new DailyBackupResult
                {
                    Outcome = DailyBackupOutcome.Failed,
                    CheckedAtUtc = checkedAt,
                    LocalDate = localDate,
                    Backup = backup,
                    Error = backup.Error ?? new BackupError(
                        BackupErrorCode.PackageWriteFailed,
                        "Daily backup failed."),
                    DurationMilliseconds = ElapsedMilliseconds(startedAt)
                };
            }

            string? stateWarning = null;
            var baselineFingerprint = backup.Snapshot?.ToFingerprint();
            if (baselineFingerprint is null)
            {
                // The backup package itself is valid but carries no marker
                // (e.g. a schema v1 snapshot). Advancing the baseline would
                // risk skipping later changes, so leave the state untouched.
                stateWarning = "snapshot_marker_missing";
                _logger.LogWarning(
                    "Daily backup succeeded but the snapshot has no change marker; the schedule baseline was not advanced.");
            }
            else
            {
                try
                {
                    await _stateStore.WriteAsync(
                            backupDirectory,
                            new BackupScheduleState
                            {
                                FormatVersion = BackupScheduleState.CurrentFormatVersion,
                                LastSuccessfulFingerprint = baselineFingerprint,
                                LastSuccessfulDailyAtUtc = backup.Manifest.CreatedAtUtc,
                                LastSuccessfulDailyLocalDate = DateOnly.FromDateTime(
                                    backup.Manifest.CreatedAtLocal.DateTime)
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (BackupException exception)
                {
                    stateWarning = $"state_write_failed:{exception.ErrorCode}";
                    _logger.LogWarning(
                        "Daily backup succeeded but the schedule state could not be written: {ErrorCode}.",
                        exception.ErrorCode);
                }
            }

            BackupRetentionResult retention;
            try
            {
                var retentionCatalog = await _catalog
                    .ReadForRetentionAsync(backupDirectory, cancellationToken)
                    .ConfigureAwait(false);
                if (retentionCatalog.Issues.Count > 0)
                {
                    _logger.LogWarning(
                        "Backup catalog reported {IssueCount} invalid package(s) during retention.",
                        retentionCatalog.Issues.Count);
                }

                var plan = _retentionPolicy.CreatePlan(
                    retentionCatalog.ValidEntries,
                    new[] { backup.PackagePath });
                retention = await _retentionStore
                    .DeleteAsync(backupDirectory, plan.Delete, cancellationToken)
                    .ConfigureAwait(false);
                _logger.LogInformation(
                    "Retention kept {KeepCount} packages and deleted {DeleteCount} with {WarningCount} warnings.",
                    plan.Keep.Count,
                    retention.DeletedPackagePaths.Count,
                    retention.Warnings.Count);
            }
            catch (BackupException exception)
            {
                _logger.LogWarning(
                    "Daily backup succeeded but retention failed: {ErrorCode}.",
                    exception.ErrorCode);
                retention = new BackupRetentionResult(
                    Array.Empty<string>(),
                    new[]
                    {
                        new BackupRetentionWarning(
                            backup.PackagePath,
                            "retention_failed",
                            $"Retention failed: {exception.ErrorCode}.")
                    });
            }

            var duration = ElapsedMilliseconds(startedAt);
            _logger.LogInformation(
                "Daily backup {PackageFileName} completed in {DurationMs} ms.",
                Path.GetFileName(backup.PackagePath),
                duration);
            return new DailyBackupResult
            {
                Outcome = DailyBackupOutcome.BackedUp,
                CheckedAtUtc = checkedAt,
                LocalDate = localDate,
                Backup = backup,
                Retention = retention,
                StateWarning = stateWarning,
                DurationMilliseconds = duration
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Unexpected error during daily backup check.");
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RunPeriodicAsync(
        string backupDirectory,
        TimeSpan interval,
        CancellationToken cancellationToken = default)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(interval),
                interval,
                "Interval must be positive.");
        }

        using var timer = new PeriodicTimer(interval);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunCheckAsync(backupDirectory, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Daily backup check failed unexpectedly; retrying after the next interval.");
            }

            if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                break;
            }
        }
    }

    private static DailyBackupResult CreateFailure(
        DateTimeOffset checkedAt,
        DateOnly localDate,
        BackupException exception,
        long startedAt)
    {
        var code = exception.ErrorCode switch
        {
            BackupErrorCodes.SourceUnavailable => BackupErrorCode.SourceUnavailable,
            BackupErrorCodes.DestinationUnavailable => BackupErrorCode.DestinationUnavailable,
            _ => BackupErrorCode.SnapshotFailed
        };
        var isRetryable = code == BackupErrorCode.DestinationUnavailable
            || exception.IsRetryable;
        return new DailyBackupResult
        {
            Outcome = DailyBackupOutcome.Failed,
            CheckedAtUtc = checkedAt,
            LocalDate = localDate,
            Error = new BackupError(
                code,
                $"Daily backup check failed: {exception.ErrorCode}.",
                isRetryable,
                $"operation={exception.Operation}; error={exception.ErrorCode}; detail={exception.Message}"),
            DurationMilliseconds = ElapsedMilliseconds(startedAt)
        };
    }

    private static long ElapsedMilliseconds(long startedAt) =>
        (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
}
