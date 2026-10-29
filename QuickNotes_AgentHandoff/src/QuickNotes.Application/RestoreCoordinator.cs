using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace QuickNotes.Application;

public sealed class RestoreCoordinator : IRestoreCoordinator
{
    public const long SupportedSchemaVersion = 2;
    private const long MinimumSourceSchemaVersion = 1;
    private const long SpaceSafetyMarginBytes = 16L * 1024 * 1024;

    private readonly IBackupPackageExtractor _extractor;
    private readonly IRestoreStore _restoreStore;
    private readonly IBackupCoordinator _backups;
    private readonly INoteDraftStore _drafts;
    private readonly IDiskSpaceProvider _diskSpace;
    private readonly IDatabaseWriteGate _writeGate;
    private readonly ISchemaMigrator _migrator;
    private readonly ILogger<RestoreCoordinator> _logger;

    public RestoreCoordinator(
        IBackupPackageExtractor extractor,
        IRestoreStore restoreStore,
        IBackupCoordinator backups,
        INoteDraftStore drafts,
        IDiskSpaceProvider diskSpace,
        IDatabaseWriteGate writeGate,
        ISchemaMigrator migrator,
        ILogger<RestoreCoordinator>? logger = null)
    {
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        _restoreStore = restoreStore ?? throw new ArgumentNullException(nameof(restoreStore));
        _backups = backups ?? throw new ArgumentNullException(nameof(backups));
        _drafts = drafts ?? throw new ArgumentNullException(nameof(drafts));
        _diskSpace = diskSpace ?? throw new ArgumentNullException(nameof(diskSpace));
        _writeGate = writeGate ?? throw new ArgumentNullException(nameof(writeGate));
        _migrator = migrator ?? throw new ArgumentNullException(nameof(migrator));
        if (writeGate is NoopDatabaseWriteGate)
        {
            throw new ArgumentException(
                "RestoreCoordinator requires a serializing write gate.",
                nameof(writeGate));
        }

        _logger = logger ?? NullLogger<RestoreCoordinator>.Instance;
    }

    public async Task<RestorePreflightResult> PreflightAsync(
        RestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var validation = ValidateRequestPaths(request);
        if (validation is not null)
        {
            return validation;
        }

        if (HasWalSidecars(request.CurrentDatabasePath))
        {
            return FailedPreflight(
                RestoreErrorCode.CurrentDatabaseUnavailable,
                "WAL sidecar files are not supported by this restore path.",
                diagnostics: RestoreErrorCodes.WalUnsupported);
        }

        if (!await CanWriteToDirectoryAsync(request.BackupDirectory, cancellationToken)
                .ConfigureAwait(false))
        {
            return FailedPreflight(
                RestoreErrorCode.DestinationUnavailable,
                "The backup directory is not writable.",
                isRetryable: true);
        }

        RestoreDatabaseInfo currentInfo;
        try
        {
            currentInfo = await _restoreStore
                .InspectAsync(request.CurrentDatabasePath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RestoreException exception)
        {
            return FailedPreflight(
                RestoreErrorCode.CurrentDatabaseUnavailable,
                "The current database could not be inspected.",
                diagnostics: exception.ErrorCode);
        }

        if (currentInfo.SchemaVersion != SupportedSchemaVersion)
        {
            return FailedPreflight(
                RestoreErrorCode.SchemaIncompatible,
                "The current database schema is not supported.",
                diagnostics: $"current_schema={currentInfo.SchemaVersion}");
        }

        var currentDirectory = Path.GetDirectoryName(
            Path.GetFullPath(request.CurrentDatabasePath))!;
        if (!await CanWriteToDirectoryAsync(currentDirectory, cancellationToken)
                .ConfigureAwait(false))
        {
            return FailedPreflight(
                RestoreErrorCode.StagingUnavailable,
                "The current database directory is not writable.",
                diagnostics: "current_directory_not_writable",
                currentCounts: currentInfo.Counts);
        }

        var preflightDirectory = Path.Combine(
            Path.GetTempPath(),
            "QuickNotes-Restore-Preflight",
            Guid.NewGuid().ToString("N"));
        try
        {
            try
            {
                Directory.CreateDirectory(preflightDirectory);
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException or ArgumentException)
            {
                return FailedPreflight(
                    RestoreErrorCode.StagingUnavailable,
                    "The restore staging directory could not be created.",
                    diagnostics: exception.GetType().Name);
            }

            var preflightDatabase = Path.Combine(preflightDirectory, "restore.db");
            BackupManifest manifest;
            try
            {
                manifest = await _extractor
                    .ExtractAsync(request.PackagePath, preflightDatabase, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (BackupException exception)
            {
                return FailedPreflight(
                    exception.ErrorCode == BackupErrorCodes.DestinationUnavailable
                        ? RestoreErrorCode.StagingUnavailable
                        : RestoreErrorCode.PackageInvalid,
                    "The backup package could not be extracted and verified.",
                    diagnostics: exception.ErrorCode,
                    currentCounts: currentInfo.Counts);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return FailedPreflight(
                    RestoreErrorCode.StagingUnavailable,
                    "The backup package could not be staged.",
                    diagnostics: exception.GetType().Name,
                    currentCounts: currentInfo.Counts);
            }

            if (manifest.SchemaVersion is < MinimumSourceSchemaVersion or > SupportedSchemaVersion)
            {
                return FailedPreflight(
                    RestoreErrorCode.SchemaIncompatible,
                    "The backup schema is not supported.",
                    diagnostics: $"backup_schema={manifest.SchemaVersion}",
                    manifest: manifest,
                    currentCounts: currentInfo.Counts);
            }

            RestoreDatabaseInfo backupInfo;
            try
            {
                backupInfo = await _restoreStore
                    .InspectAsync(preflightDatabase, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (RestoreException exception)
            {
                return FailedPreflight(
                    RestoreErrorCode.PackageInvalid,
                    "The staged backup database could not be inspected.",
                    diagnostics: exception.ErrorCode,
                    manifest: manifest,
                    currentCounts: currentInfo.Counts);
            }

            if (backupInfo.SchemaVersion != manifest.SchemaVersion)
            {
                return FailedPreflight(
                    RestoreErrorCode.PackageInvalid,
                    "The staged backup schema does not match its manifest.",
                    manifest: manifest,
                    backupCounts: backupInfo.Counts,
                    currentCounts: currentInfo.Counts);
            }

            var requiresMigration = manifest.SchemaVersion < SupportedSchemaVersion;
            string? targetHash;
            try
            {
                await _migrator
                    .MigrateToCurrentAsync(preflightDatabase, rotateIdentity: true, cancellationToken)
                    .ConfigureAwait(false);
                backupInfo = await _restoreStore
                    .InspectAsync(preflightDatabase, cancellationToken)
                    .ConfigureAwait(false);
                if (backupInfo.SchemaVersion != SupportedSchemaVersion)
                {
                    return FailedPreflight(
                        RestoreErrorCode.PackageInvalid,
                        "The prepared backup schema is not supported.",
                        diagnostics: $"prepared_schema={backupInfo.SchemaVersion}",
                        manifest: manifest,
                        backupCounts: backupInfo.Counts,
                        currentCounts: currentInfo.Counts);
                }

                targetHash = await _restoreStore
                    .TryGetDatabaseHashAsync(preflightDatabase, cancellationToken)
                    .ConfigureAwait(false);
                if (targetHash is null)
                {
                    return FailedPreflight(
                        RestoreErrorCode.PackageInvalid,
                        "The prepared backup database could not be hashed.",
                        diagnostics: "prepared_hash_unavailable",
                        manifest: manifest,
                        backupCounts: backupInfo.Counts,
                        currentCounts: currentInfo.Counts);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return FailedPreflight(
                    RestoreErrorCode.PackageInvalid,
                    "The backup could not be prepared for the current schema.",
                    diagnostics: $"migration_failed:{exception.GetType().Name}",
                    manifest: manifest,
                    backupCounts: backupInfo.Counts,
                    currentCounts: currentInfo.Counts);
            }

            int pendingDraftCount;
            try
            {
                var drafts = await _drafts.ListAsync(noteId: null, cancellationToken)
                    .ConfigureAwait(false);
                pendingDraftCount = drafts.Count;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return FailedPreflight(
                    RestoreErrorCode.DraftCheckFailed,
                    "Pending drafts could not be checked.",
                    diagnostics: exception.GetType().Name,
                    manifest: manifest,
                    backupCounts: backupInfo.Counts,
                    currentCounts: currentInfo.Counts);
            }

            long currentLength;
            try
            {
                currentLength = new FileInfo(request.CurrentDatabasePath).Length;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return FailedPreflight(
                    RestoreErrorCode.CurrentDatabaseUnavailable,
                    "The current database could not be read.",
                    diagnostics: exception.GetType().Name,
                    manifest: manifest,
                    backupCounts: backupInfo.Counts,
                    currentCounts: currentInfo.Counts,
                    pendingDraftCount: pendingDraftCount);
            }

            long requiredCurrent;
            long requiredBackup;
            try
            {
                requiredCurrent = checked(
                    manifest.DatabaseSizeBytes + currentLength + SpaceSafetyMarginBytes);
                requiredBackup = checked(currentLength + SpaceSafetyMarginBytes);
            }
            catch (OverflowException)
            {
                return FailedPreflight(
                    RestoreErrorCode.InsufficientSpace,
                    "The restore size exceeds the supported range.",
                    manifest: manifest,
                    backupCounts: backupInfo.Counts,
                    currentCounts: currentInfo.Counts,
                    pendingDraftCount: pendingDraftCount);
            }

            var currentRoot = Path.GetPathRoot(
                Path.GetFullPath(request.CurrentDatabasePath));
            var backupRoot = Path.GetPathRoot(
                Path.GetFullPath(request.BackupDirectory));
            var sameVolume = string.Equals(
                currentRoot,
                backupRoot,
                StringComparison.OrdinalIgnoreCase);
            if (sameVolume)
            {
                try
                {
                    requiredCurrent = checked(requiredCurrent + requiredBackup);
                }
                catch (OverflowException)
                {
                    return FailedPreflight(
                        RestoreErrorCode.InsufficientSpace,
                        "The restore size exceeds the supported range.",
                        manifest: manifest,
                        backupCounts: backupInfo.Counts,
                        currentCounts: currentInfo.Counts,
                        pendingDraftCount: pendingDraftCount);
                }
            }

            long availableCurrent;
            long availableBackup;
            try
            {
                availableCurrent = _diskSpace.GetAvailableBytes(request.CurrentDatabasePath);
                availableBackup = _diskSpace.GetAvailableBytes(request.BackupDirectory);
            }
            catch (RestoreException exception)
            {
                return FailedPreflight(
                    RestoreErrorCode.InsufficientSpace,
                    "Available space could not be determined.",
                    diagnostics: exception.ErrorCode,
                    manifest: manifest,
                    backupCounts: backupInfo.Counts,
                    currentCounts: currentInfo.Counts,
                    pendingDraftCount: pendingDraftCount);
            }

            if (availableCurrent < requiredCurrent
                || (!sameVolume && availableBackup < requiredBackup))
            {
                return FailedPreflight(
                    RestoreErrorCode.InsufficientSpace,
                    "There is not enough free space for the restore.",
                    diagnostics:
                        $"current_required={requiredCurrent};current_available={availableCurrent};backup_required={requiredBackup};backup_available={availableBackup};same_volume={sameVolume}",
                    manifest: manifest,
                    backupCounts: backupInfo.Counts,
                    currentCounts: currentInfo.Counts,
                    pendingDraftCount: pendingDraftCount,
                    requiredBytes: requiredCurrent,
                    availableBytes: availableCurrent);
            }

            if (pendingDraftCount > 0 && !request.AllowPendingDrafts)
            {
                return FailedPreflight(
                    RestoreErrorCode.PendingDrafts,
                    "Pending drafts must be acknowledged before restoring.",
                    manifest: manifest,
                    backupCounts: backupInfo.Counts,
                    currentCounts: currentInfo.Counts,
                    pendingDraftCount: pendingDraftCount,
                    requiredBytes: requiredCurrent,
                    availableBytes: availableCurrent);
            }

            return new RestorePreflightResult
            {
                CanProceed = true,
                Manifest = manifest,
                BackupCounts = backupInfo.Counts,
                CurrentCounts = currentInfo.Counts,
                PendingDraftCount = pendingDraftCount,
                RequiresMigration = requiresMigration,
                TargetSchemaVersion = SupportedSchemaVersion,
                SourceManifestSha256 = manifest.DatabaseSha256,
                TargetDatabaseSha256 = targetHash,
                RequiredBytes = requiredCurrent,
                AvailableBytes = availableCurrent
            };
        }
        finally
        {
            CleanupDirectory(preflightDirectory);
        }
    }

    public async Task<RestoreResult> RestoreAsync(
        RestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var startedAt = Stopwatch.GetTimestamp();
        var preflight = await PreflightAsync(request, cancellationToken).ConfigureAwait(false);
        if (!preflight.CanProceed)
        {
            return new RestoreResult
            {
                Status = RestoreStatus.PreflightFailed,
                Manifest = preflight.Manifest,
                Error = preflight.Error,
                DurationMilliseconds = ElapsedMilliseconds(startedAt)
            };
        }

        IDatabaseWriteLease lease;
        try
        {
            lease = await _writeGate.EnterRestoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DatabaseWriteBlockedException)
        {
            return new RestoreResult
            {
                Status = RestoreStatus.PreflightFailed,
                Manifest = preflight.Manifest,
                Error = new RestoreError(
                    RestoreErrorCode.WritesBlocked,
                    "Database writes are blocked after a failed restore rollback.",
                    false,
                    "database_writes_blocked"),
                WritesBlocked = true,
                DurationMilliseconds = ElapsedMilliseconds(startedAt)
            };
        }

        try
        {
            await using (lease)
            {
                return await RestoreCoreAsync(request, preflight, startedAt, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                "Unexpected error during database restore ({ExceptionType}).",
                exception.GetType().Name);
            throw;
        }
    }

    private async Task<RestoreResult> RestoreCoreAsync(
        RestoreRequest request,
        RestorePreflightResult preflight,
        long startedAt,
        CancellationToken cancellationToken)
    {
        RestoreDatabaseInfo currentInfo;
        try
        {
            currentInfo = await _restoreStore
                .PrepareCurrentAsync(request.CurrentDatabasePath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RestoreException exception)
        {
            return Failure(
                RestoreStatus.PreflightFailed,
                RestoreErrorCode.CurrentDatabaseUnavailable,
                "The current database could not be prepared for restore.",
                diagnostics: exception.ErrorCode,
                startedAt: startedAt,
                manifest: preflight.Manifest);
        }

        if (currentInfo.SchemaVersion != SupportedSchemaVersion)
        {
            return Failure(
                RestoreStatus.PreflightFailed,
                RestoreErrorCode.SchemaIncompatible,
                "The current database schema is not supported.",
                diagnostics: $"current_schema={currentInfo.SchemaVersion}",
                startedAt: startedAt,
                manifest: preflight.Manifest);
        }

        var currentHash = await _restoreStore
            .TryGetDatabaseHashAsync(request.CurrentDatabasePath, cancellationToken)
            .ConfigureAwait(false);
        if (currentHash is null)
        {
            return Failure(
                RestoreStatus.PreflightFailed,
                RestoreErrorCode.CurrentDatabaseUnavailable,
                "The current database could not be hashed.",
                diagnostics: RestoreErrorCodes.CurrentDatabaseUnavailable,
                startedAt: startedAt,
                manifest: preflight.Manifest);
        }

        BackupResult protection;
        try
        {
            protection = await _backups.CreateAsync(
                    new BackupRequest
                    {
                        BackupDirectory = request.BackupDirectory,
                        Type = BackupType.Manual
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (BackupException exception)
        {
            return Failure(
                RestoreStatus.ProtectionFailed,
                RestoreErrorCode.ProtectionSnapshotFailed,
                "The current database protection snapshot could not be created.",
                diagnostics: exception.ErrorCode,
                startedAt: startedAt,
                manifest: preflight.Manifest);
        }

        var protectionPath = protection.PackagePath;
        if (protection.Status != BackupStatus.Succeeded || protectionPath is null)
        {
            return Failure(
                RestoreStatus.ProtectionFailed,
                RestoreErrorCode.ProtectionSnapshotFailed,
                "The current database protection snapshot failed.",
                diagnostics: protection.Error?.Diagnostics,
                startedAt: startedAt,
                manifest: preflight.Manifest,
                protectionSnapshotPath: protectionPath);
        }

        var currentDirectory = Path.GetDirectoryName(
            Path.GetFullPath(request.CurrentDatabasePath))!;
        var stagingDirectory = Path.Combine(
            currentDirectory,
            $".quicknotes-restore-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(stagingDirectory);
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetAttributes(
                        stagingDirectory,
                        File.GetAttributes(stagingDirectory) | FileAttributes.Hidden);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Failure(
                RestoreStatus.PreflightFailed,
                RestoreErrorCode.StagingUnavailable,
                "The restore staging directory could not be created.",
                diagnostics: exception.GetType().Name,
                startedAt: startedAt,
                manifest: preflight.Manifest,
                protectionSnapshotPath: protectionPath);
        }

        var stagedPath = Path.Combine(stagingDirectory, "restored.db");
        var rollbackPath = Path.Combine(stagingDirectory, "rollback.db");
        var switchStarted = false;
        try
        {
            BackupManifest manifest;
            try
            {
                manifest = await _extractor
                    .ExtractAsync(request.PackagePath, stagedPath, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                CleanupDirectory(stagingDirectory);
                throw;
            }
            catch (BackupException exception)
            {
                CleanupDirectory(stagingDirectory);
                return Failure(
                    RestoreStatus.VerificationFailed,
                    exception.ErrorCode == BackupErrorCodes.DestinationUnavailable
                        ? RestoreErrorCode.StagingUnavailable
                        : RestoreErrorCode.PackageInvalid,
                    "The backup package could not be extracted and verified.",
                    diagnostics: exception.ErrorCode,
                    startedAt: startedAt,
                    manifest: preflight.Manifest,
                    protectionSnapshotPath: protectionPath);
            }

            if (manifest.SchemaVersion is < MinimumSourceSchemaVersion or > SupportedSchemaVersion)
            {
                CleanupDirectory(stagingDirectory);
                return Failure(
                    RestoreStatus.VerificationFailed,
                    RestoreErrorCode.SchemaIncompatible,
                    "The backup schema is not supported.",
                    diagnostics: $"backup_schema={manifest.SchemaVersion}",
                    startedAt: startedAt,
                    manifest: manifest,
                    protectionSnapshotPath: protectionPath);
            }

            RestoreDatabaseInfo stagedInfo;
            string? targetHash;
            try
            {
                await _migrator
                    .MigrateToCurrentAsync(stagedPath, rotateIdentity: true, cancellationToken)
                    .ConfigureAwait(false);
                stagedInfo = await _restoreStore
                    .InspectAsync(stagedPath, cancellationToken)
                    .ConfigureAwait(false);
                if (stagedInfo.SchemaVersion != SupportedSchemaVersion)
                {
                    CleanupDirectory(stagingDirectory);
                    return Failure(
                        RestoreStatus.VerificationFailed,
                        RestoreErrorCode.SchemaIncompatible,
                        "The staged backup schema is not supported.",
                        diagnostics: $"staged_schema={stagedInfo.SchemaVersion}",
                        startedAt: startedAt,
                        manifest: manifest,
                        protectionSnapshotPath: protectionPath);
                }

                targetHash = await _restoreStore
                    .TryGetDatabaseHashAsync(stagedPath, cancellationToken)
                    .ConfigureAwait(false);
                if (targetHash is null)
                {
                    CleanupDirectory(stagingDirectory);
                    return Failure(
                        RestoreStatus.VerificationFailed,
                        RestoreErrorCode.PackageInvalid,
                        "The staged backup database could not be hashed.",
                        diagnostics: "prepared_hash_unavailable",
                        startedAt: startedAt,
                        manifest: manifest,
                        protectionSnapshotPath: protectionPath);
                }
            }
            catch (OperationCanceledException)
            {
                CleanupDirectory(stagingDirectory);
                throw;
            }
            catch (Exception exception)
            {
                CleanupDirectory(stagingDirectory);
                return Failure(
                    RestoreStatus.VerificationFailed,
                    RestoreErrorCode.PackageInvalid,
                    "The backup could not be prepared for the current schema.",
                    diagnostics: $"migration_failed:{exception.GetType().Name}",
                    startedAt: startedAt,
                    manifest: manifest,
                    protectionSnapshotPath: protectionPath);
            }

            cancellationToken.ThrowIfCancellationRequested();

            var switched = false;
            try
            {
                switchStarted = true;
                await _restoreStore
                    .SwitchAsync(
                        stagedPath,
                        request.CurrentDatabasePath,
                        rollbackPath,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                switched = true;
            }
            catch (RestoreException switchException)
            {
                var currentAfter = await TryInspectAsync(request.CurrentDatabasePath)
                    .ConfigureAwait(false);
                var currentAfterHash = await _restoreStore
                    .TryGetDatabaseHashAsync(
                        request.CurrentDatabasePath,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (currentAfter is not null
                    && Matches(currentAfter, stagedInfo)
                    && string.Equals(
                        currentAfterHash,
                        targetHash!,
                        StringComparison.Ordinal))
                {
                    switched = true;
                    _logger.LogWarning(
                        "Restore switch reported an error but the restored database is present: {ErrorCode}.",
                        switchException.ErrorCode);
                }
                else if (currentAfter is not null
                    && Matches(currentAfter, currentInfo)
                    && string.Equals(
                        currentAfterHash,
                        currentHash,
                        StringComparison.Ordinal))
                {
                    CleanupDirectory(stagingDirectory);
                    return Failure(
                        RestoreStatus.SwitchFailed,
                        RestoreErrorCode.SwitchFailed,
                        "The restored database could not be switched in; the current database is unchanged.",
                        diagnostics: switchException.ErrorCode,
                        startedAt: startedAt,
                        manifest: manifest,
                        protectionSnapshotPath: protectionPath);
                }
                else
                {
                    return await RollbackAfterFailureAsync(
                            request,
                            currentInfo,
                            currentHash,
                            manifest,
                            protectionPath,
                            stagingDirectory,
                            rollbackPath,
                            RestoreStatus.SwitchFailed,
                            RestoreErrorCode.SwitchFailed,
                            startedAt)
                        .ConfigureAwait(false);
                }
            }

            if (!switched)
            {
                return await RollbackAfterFailureAsync(
                        request,
                        currentInfo,
                        currentHash,
                        manifest,
                        protectionPath,
                        stagingDirectory,
                        rollbackPath,
                        RestoreStatus.SwitchFailed,
                        RestoreErrorCode.SwitchFailed,
                        startedAt)
                    .ConfigureAwait(false);
            }

            var restored = await TryInspectAsync(request.CurrentDatabasePath)
                .ConfigureAwait(false);
            var restoredHash = await _restoreStore
                .TryGetDatabaseHashAsync(
                    request.CurrentDatabasePath,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (restored is not null
                && Matches(restored, stagedInfo)
                && string.Equals(
                    restoredHash,
                    targetHash!,
                    StringComparison.Ordinal))
            {
                CleanupDirectory(stagingDirectory);
                var duration = ElapsedMilliseconds(startedAt);
                _logger.LogInformation(
                    "Database restore succeeded with {NoteCount} notes; protection snapshot {ProtectionFileName}.",
                    restored.Counts.ActiveNotes + restored.Counts.ArchivedNotes + restored.Counts.TrashNotes,
                    Path.GetFileName(protectionPath));
                return new RestoreResult
                {
                    Status = RestoreStatus.Succeeded,
                    Manifest = manifest,
                    RestoredCounts = restored.Counts,
                    ProtectionSnapshotPath = protectionPath,
                    MigratedFromV1 = manifest.SchemaVersion < SupportedSchemaVersion,
                    TargetSchemaVersion = SupportedSchemaVersion,
                    TargetDatabaseSha256 = targetHash,
                    DurationMilliseconds = duration
                };
            }

            return await RollbackAfterFailureAsync(
                    request,
                    currentInfo,
                    currentHash,
                    manifest,
                    protectionPath,
                    stagingDirectory,
                    rollbackPath,
                    RestoreStatus.SwitchFailed,
                    RestoreErrorCode.PostRestoreVerificationFailed,
                    startedAt)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            CleanupDirectory(stagingDirectory);
            throw;
        }
        catch (Exception exception) when (switchStarted)
        {
            _writeGate.BlockWrites();
            _logger.LogError(
                "Unexpected restore failure after the switch started ({ExceptionType}); database writes are blocked.",
                exception.GetType().Name);
            return Failure(
                RestoreStatus.RollbackFailed,
                RestoreErrorCode.RollbackFailed,
                "The restore failed after the switch started and the database state could not be verified. Database writes are blocked.",
                diagnostics: exception.GetType().Name,
                startedAt: startedAt,
                manifest: preflight.Manifest,
                protectionSnapshotPath: protectionPath,
                writesBlocked: true,
                recoveryHint: BuildRecoveryHint(
                    request.CurrentDatabasePath,
                    protectionPath,
                    rollbackPath));
        }
    }

    private async Task<RestoreResult> RollbackAfterFailureAsync(
        RestoreRequest request,
        RestoreDatabaseInfo currentInfo,
        string currentHash,
        BackupManifest manifest,
        string protectionPath,
        string stagingDirectory,
        string rollbackPath,
        RestoreStatus failureStatus,
        RestoreErrorCode errorCode,
        long startedAt)
    {
        var failedPath = Path.Combine(stagingDirectory, "failed.db");
        try
        {
            await _restoreStore
                .RollbackAsync(
                    rollbackPath,
                    request.CurrentDatabasePath,
                    failedPath,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception rollbackException)
        {
            _writeGate.BlockWrites();
            var diagnostics = rollbackException is RestoreException restoreException
                ? restoreException.ErrorCode
                : rollbackException.GetType().Name;
            _logger.LogError(
                "Restore rollback failed ({Diagnostics}); database writes are blocked.",
                diagnostics);
            return Failure(
                RestoreStatus.RollbackFailed,
                RestoreErrorCode.RollbackFailed,
                "The restore failed and the previous database could not be rolled back. Database writes are blocked.",
                diagnostics: diagnostics,
                startedAt: startedAt,
                manifest: manifest,
                protectionSnapshotPath: protectionPath,
                writesBlocked: true,
                recoveryHint: BuildRecoveryHint(
                    request.CurrentDatabasePath,
                    protectionPath,
                    rollbackPath));
        }

        var restoredOld = await TryInspectAsync(request.CurrentDatabasePath)
            .ConfigureAwait(false);
        var oldHash = await _restoreStore
            .TryGetDatabaseHashAsync(
                request.CurrentDatabasePath,
                CancellationToken.None)
            .ConfigureAwait(false);
        if (restoredOld is not null
            && Matches(restoredOld, currentInfo)
            && string.Equals(oldHash, currentHash, StringComparison.Ordinal))
        {
            CleanupDirectory(stagingDirectory);
            _logger.LogWarning(
                "Restore failed with {ErrorCode}; the previous database was rolled back.",
                errorCode);
            return Failure(
                failureStatus,
                errorCode,
                "The restore failed and the previous database was restored.",
                startedAt: startedAt,
                manifest: manifest,
                protectionSnapshotPath: protectionPath,
                rolledBack: true);
        }

        _writeGate.BlockWrites();
        _logger.LogError(
            "Restore rollback completed but the previous database could not be verified; database writes are blocked.");
        return Failure(
            RestoreStatus.RollbackFailed,
            RestoreErrorCode.RollbackFailed,
            "The restore failed and the previous database could not be verified. Database writes are blocked.",
            startedAt: startedAt,
            manifest: manifest,
            protectionSnapshotPath: protectionPath,
            writesBlocked: true,
            recoveryHint: BuildRecoveryHint(
                request.CurrentDatabasePath,
                protectionPath,
                rollbackPath));
    }

    private async Task<RestoreDatabaseInfo?> TryInspectAsync(string databasePath)
    {
        try
        {
            return await _restoreStore.InspectAsync(databasePath, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Database inspection failed during restore ({ExceptionType}).",
                exception.GetType().Name);
            return null;
        }
    }

    private RestorePreflightResult? ValidateRequestPaths(RestoreRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PackagePath)
            || string.IsNullOrWhiteSpace(request.CurrentDatabasePath)
            || string.IsNullOrWhiteSpace(request.BackupDirectory))
        {
            return FailedPreflight(
                RestoreErrorCode.ValidationFailed,
                "Restore request paths must not be empty.");
        }

        if (!File.Exists(request.PackagePath))
        {
            return FailedPreflight(
                RestoreErrorCode.PackageNotFound,
                "The backup package does not exist.");
        }

        if (!File.Exists(request.CurrentDatabasePath))
        {
            return FailedPreflight(
                RestoreErrorCode.CurrentDatabaseUnavailable,
                "The current database does not exist.");
        }

        if (!Directory.Exists(request.BackupDirectory))
        {
            return FailedPreflight(
                RestoreErrorCode.DestinationUnavailable,
                "The backup directory does not exist.",
                isRetryable: true);
        }

        return null;
    }

    private static bool HasWalSidecars(string databasePath) =>
        File.Exists(databasePath + "-wal") || File.Exists(databasePath + "-shm");

    private static async Task<bool> CanWriteToDirectoryAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        var probePath = Path.Combine(
            directory,
            $".quicknotes-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(probePath, Array.Empty<byte>(), cancellationToken)
                .ConfigureAwait(false);
            File.Delete(probePath);
            return true;
        }
        catch (OperationCanceledException)
        {
            TryDelete(probePath);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDelete(probePath);
            return false;
        }
    }

    private static bool Matches(RestoreDatabaseInfo left, RestoreDatabaseInfo right) =>
        left.SchemaVersion == right.SchemaVersion
        && left.Counts == right.Counts;

    private static RestorePreflightResult FailedPreflight(
        RestoreErrorCode code,
        string message,
        bool isRetryable = false,
        string? diagnostics = null,
        BackupManifest? manifest = null,
        RestoreCounts? backupCounts = null,
        RestoreCounts? currentCounts = null,
        int pendingDraftCount = 0,
        long requiredBytes = 0,
        long availableBytes = 0) => new()
    {
        CanProceed = false,
        Manifest = manifest,
        BackupCounts = backupCounts,
        CurrentCounts = currentCounts,
        PendingDraftCount = pendingDraftCount,
        RequiredBytes = requiredBytes,
        AvailableBytes = availableBytes,
        Error = new RestoreError(code, message, isRetryable, diagnostics)
    };

    private static RestoreResult Failure(
        RestoreStatus status,
        RestoreErrorCode code,
        string message,
        string? diagnostics = null,
        long startedAt = 0,
        BackupManifest? manifest = null,
        string? protectionSnapshotPath = null,
        bool rolledBack = false,
        bool writesBlocked = false,
        string? recoveryHint = null) => new()
    {
        Status = status,
        Manifest = manifest,
        ProtectionSnapshotPath = protectionSnapshotPath,
        Error = new RestoreError(code, message, false, diagnostics),
        RolledBack = rolledBack,
        WritesBlocked = writesBlocked,
        RecoveryHint = recoveryHint,
        DurationMilliseconds = startedAt == 0 ? 0 : ElapsedMilliseconds(startedAt)
    };

    private static string BuildRecoveryHint(
        string currentDatabasePath,
        string protectionSnapshotPath,
        string rollbackDatabasePath) =>
        $"Manual recovery is required. Current database: {currentDatabasePath}; protection backup: {protectionSnapshotPath}; rollback database: {rollbackDatabasePath}.";

    private void CleanupDirectory(string directory)
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
            _logger.LogWarning(
                "Temporary restore directory {DirectoryName} could not be removed: {ExceptionType}.",
                Path.GetFileName(directory),
                exception.GetType().Name);
        }
    }

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

    private static long ElapsedMilliseconds(long startedAt) =>
        (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
}
