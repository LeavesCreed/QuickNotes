using QuickNotes.Application;
using QuickNotes.Desktop.Diagnostics;
using QuickNotes.Desktop.Paths;
using QuickNotes.Desktop.State;

namespace QuickNotes.Desktop.Recovery;

public sealed class MissingDatabaseRebuilder
{
    private const long SupportedSchemaVersion = 1;

    private readonly DesktopPaths _paths;
    private readonly IBackupPackageExtractor _extractor;
    private readonly IRestoreStore _restoreStore;
    private readonly ApplicationStateStore _stateStore;
    private readonly DiagnosticLog _log;
    private readonly TimeProvider _timeProvider;

    public MissingDatabaseRebuilder(
        DesktopPaths paths,
        IBackupPackageExtractor extractor,
        IRestoreStore restoreStore,
        ApplicationStateStore stateStore,
        DiagnosticLog log,
        TimeProvider? timeProvider = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        _restoreStore = restoreStore ?? throw new ArgumentNullException(nameof(restoreStore));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<MissingDatabaseRebuildResult> RebuildAsync(
        string packagePath,
        bool quarantineCorruptCurrent,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
        {
            return Failed("package_not_found", "The selected backup package does not exist.");
        }

        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var stateResult = await _stateStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (stateResult.Status != ApplicationStateReadStatus.Found)
        {
            return Failed("state_not_ready", "The application state is not ready for recovery.");
        }

        var state = stateResult.State!;
        if (state.InitializationStatus != ApplicationInitializationStatus.Ready)
        {
            return Failed("state_not_ready", "The application is not initialized.");
        }

        if (state.WritesBlocked)
        {
            return Failed(
                "writes_blocked",
                "Database writes are blocked; use the recovery diagnostics path first.");
        }

        if (File.Exists(_paths.DatabasePath + "-journal")
            || File.Exists(_paths.DatabasePath + "-wal")
            || File.Exists(_paths.DatabasePath + "-shm"))
        {
            return Failed(
                "current_database_sidecars",
                "The current database has sidecar files; recovery was not attempted.");
        }

        var currentExists = File.Exists(_paths.DatabasePath);
        if (currentExists)
        {
            try
            {
                var currentInfo = await _restoreStore
                    .InspectAsync(_paths.DatabasePath, cancellationToken)
                    .ConfigureAwait(false);
                if (currentInfo.SchemaVersion != SupportedSchemaVersion)
                {
                    return Failed(
                        "current_database_unsupported",
                        "The current database schema is not supported.");
                }

                return Failed(
                    "current_database_present",
                    "A valid current database exists; use the normal restore path.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (RestoreException exception)
            {
                if (!quarantineCorruptCurrent)
                {
                    _log.TryWrite(
                        "WARN",
                        "missing_database_rebuild",
                        "current database is corrupt and quarantine was not confirmed",
                        exception.ErrorCode);
                    return Failed(
                        "current_database_corrupt",
                        "The current database is corrupt; explicit quarantine confirmation is required.");
                }
            }
        }

        var stagingDirectory = Path.Combine(
            _paths.DataRoot,
            $".quicknotes-recovery-{Guid.NewGuid():N}");
        var stagedPath = Path.Combine(stagingDirectory, "notes.db");
        string? quarantinePath = null;
        string? publishedError = null;
        try
        {
            Directory.CreateDirectory(stagingDirectory);
            var manifest = await _extractor
                .ExtractAsync(packagePath, stagedPath, cancellationToken)
                .ConfigureAwait(false);
            if (manifest.SchemaVersion != SupportedSchemaVersion)
            {
                return Failed(
                    "schema_unsupported",
                    "The backup schema version is not supported.",
                    stagingDirectory);
            }

            var stagedInfo = await _restoreStore
                .InspectAsync(stagedPath, cancellationToken)
                .ConfigureAwait(false);
            if (stagedInfo.SchemaVersion != manifest.SchemaVersion
                || stagedInfo.SchemaVersion != SupportedSchemaVersion)
            {
                return Failed(
                    "package_invalid",
                    "The staged backup schema does not match its manifest.",
                    stagingDirectory);
            }

            if (currentExists)
            {
                quarantinePath = Path.Combine(
                    _paths.DataRoot,
                    $".quicknotes-quarantine-{Guid.NewGuid():N}.db");
                await _restoreStore
                    .SwitchAsync(stagedPath, _paths.DatabasePath, quarantinePath, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                if (File.Exists(_paths.DatabasePath)
                    || File.Exists(_paths.DatabasePath + "-journal")
                    || File.Exists(_paths.DatabasePath + "-wal")
                    || File.Exists(_paths.DatabasePath + "-shm"))
                {
                    return Failed(
                        "current_database_changed",
                        "The database appeared while recovery was being prepared.",
                        stagingDirectory);
                }

                File.Move(stagedPath, _paths.DatabasePath);
            }

            var verified = await VerifyPublishedAsync(
                    manifest,
                    stagedInfo,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!verified.IsVerified)
            {
                publishedError = verified.ErrorCode;
                return await HandlePostPublishFailureAsync(
                        currentExists,
                        quarantinePath,
                        stagingDirectory,
                        publishedError,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            string? stateWarning = null;
            try
            {
                await _stateStore.WriteAsync(
                        state with
                        {
                            LastStartupUtc = now,
                            RecoverySourceSha256 = manifest.DatabaseSha256,
                            RecoveryProtectionSnapshotCreated = false,
                            LastRecoveryAtUtc = now
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException or ArgumentException)
            {
                stateWarning = $"state_write_failed:{exception.GetType().Name}";
                _log.TryWrite(
                    "WARN",
                    "missing_database_rebuild",
                    "recovered database is valid but recovery metadata could not be persisted",
                    exception.GetType().Name);
            }

            TryDeleteDirectory(stagingDirectory);
            _log.TryWrite(
                "INFO",
                "missing_database_rebuild",
                currentExists
                    ? "database rebuilt from verified backup; corrupt current database quarantined"
                    : "database rebuilt from verified backup; no protection snapshot was possible");
            return new MissingDatabaseRebuildResult
            {
                Status = MissingDatabaseRebuildStatus.Succeeded,
                RestoredCounts = verified.Counts,
                StagingDirectory = stagingDirectory,
                QuarantinePath = quarantinePath,
                ProtectionSnapshotCreated = false,
                StateWarning = stateWarning
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (BackupException exception)
        {
            _log.TryWrite(
                "ERROR",
                "missing_database_rebuild",
                "backup package could not be verified",
                exception.ErrorCode);
            return Failed(
                exception.ErrorCode,
                "The backup package could not be verified.",
                stagingDirectory,
                quarantinePath);
        }
        catch (RestoreException exception)
        {
            _log.TryWrite(
                "ERROR",
                "missing_database_rebuild",
                "database publish failed",
                exception.ErrorCode);
            return Failed(
                exception.ErrorCode,
                "The recovered database could not be published.",
                stagingDirectory,
                quarantinePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.TryWrite(
                "ERROR",
                "missing_database_rebuild",
                "database publish failed",
                exception.GetType().Name);
            return Failed(
                "rebuild_io",
                "The recovered database could not be published.",
                stagingDirectory,
                quarantinePath);
        }
    }

    private async Task<(bool IsVerified, string? ErrorCode, RestoreCounts Counts)> VerifyPublishedAsync(
        BackupManifest manifest,
        RestoreDatabaseInfo stagedInfo,
        CancellationToken cancellationToken)
    {
        var hash = await _restoreStore
            .TryGetDatabaseHashAsync(_paths.DatabasePath, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(hash, manifest.DatabaseSha256, StringComparison.Ordinal))
        {
            return (false, "post_publish_hash_mismatch", stagedInfo.Counts);
        }

        var currentInfo = await _restoreStore
            .InspectAsync(_paths.DatabasePath, cancellationToken)
            .ConfigureAwait(false);
        if (currentInfo.SchemaVersion != SupportedSchemaVersion
            || currentInfo.Counts != stagedInfo.Counts)
        {
            return (false, "post_publish_verification_failed", stagedInfo.Counts);
        }

        return (true, null, currentInfo.Counts);
    }

    private async Task<MissingDatabaseRebuildResult> HandlePostPublishFailureAsync(
        bool hadCurrentDatabase,
        string? quarantinePath,
        string stagingDirectory,
        string? errorCode,
        CancellationToken cancellationToken)
    {
        var failedPath = Path.Combine(stagingDirectory, "failed-notes.db");
        if (hadCurrentDatabase && quarantinePath is not null)
        {
            try
            {
                await _restoreStore
                    .RollbackAsync(
                        quarantinePath,
                        _paths.DatabasePath,
                        failedPath,
                        cancellationToken)
                    .ConfigureAwait(false);
                _log.TryWrite(
                    "ERROR",
                    "missing_database_rebuild",
                    "recovered database failed verification; quarantined original was restored",
                    errorCode);
                return Failed(
                    errorCode ?? "post_publish_verification_failed",
                    "The recovered database failed verification and was rolled back.",
                    stagingDirectory,
                    quarantinePath);
            }
            catch (Exception exception) when (exception is RestoreException or IOException)
            {
                var blocked = _stateStore.TryMarkWritesBlocked(
                    "missing_database_rebuild_rollback_failed",
                    _timeProvider.GetUtcNow(),
                    out var stateError);
                _log.TryWrite(
                    "ERROR",
                    "missing_database_rebuild",
                    "recovered database failed verification and rollback failed",
                    exception is RestoreException restore
                        ? restore.ErrorCode
                        : exception.GetType().Name);
                return Failed(
                    "rebuild_rollback_failed",
                    "The recovered database failed verification and the previous state could not be restored.",
                    stagingDirectory,
                    quarantinePath,
                    writesBlocked: true,
                    stateWarning: blocked ? null : stateError);
            }
        }

        TryMove(_paths.DatabasePath, failedPath);
        _log.TryWrite(
            "ERROR",
            "missing_database_rebuild",
            "recovered database failed verification; failed copy preserved",
            errorCode);
        return Failed(
            errorCode ?? "post_publish_verification_failed",
            "The recovered database failed verification.",
            stagingDirectory,
            quarantinePath);
    }

    private static MissingDatabaseRebuildResult Failed(
        string errorCode,
        string message,
        string? stagingDirectory = null,
        string? quarantinePath = null,
        bool writesBlocked = false,
        string? stateWarning = null) => new()
        {
            Status = MissingDatabaseRebuildStatus.Failed,
            ErrorCode = errorCode,
            Message = message,
            StagingDirectory = stagingDirectory,
            QuarantinePath = quarantinePath,
            ProtectionSnapshotCreated = false,
            WritesBlocked = writesBlocked,
            StateWarning = stateWarning
        };

    private void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.TryWrite(
                "WARN",
                "missing_database_rebuild",
                "recovery staging directory could not be removed",
                exception.GetType().Name);
        }
    }

    private static void TryMove(string source, string destination)
    {
        try
        {
            if (File.Exists(source))
            {
                File.Move(source, destination, overwrite: false);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
