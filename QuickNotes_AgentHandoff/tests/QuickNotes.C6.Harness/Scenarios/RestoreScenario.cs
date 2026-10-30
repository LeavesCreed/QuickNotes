using QuickNotes.Application;
using QuickNotes.Desktop.Diagnostics;
using QuickNotes.Desktop.Recovery;
using QuickNotes.Desktop.SingleInstance;
using QuickNotes.Desktop.State;
using QuickNotes.Infrastructure.Backup;
using QuickNotes.Infrastructure.Drafts;
using QuickNotes.Infrastructure.Sqlite;

namespace QuickNotes.C6.Harness;

/// <summary>
/// Real restore scenario used by C6-K05/K06/K07 and the Task 4 competition
/// cases. The extraction, protection snapshot, migration, switch, and rollback
/// all run through the production restore services. Only the checkpoint
/// markers, the harness-written journal (in the production journal format that
/// the desktop host already reads at startup), the optional pause/single-
/// instance competition composition, and the optional post-switch/rollback
/// faults are C6 test composition.
///
/// <para>
/// <c>--single-instance</c> acquires the real production single-instance lock
/// and activation pipe for the disposable data root and holds them for the
/// whole restore. <c>--pause-at restore.before_switch</c> writes the checkpoint
/// marker, keeps the harness-written journal (production format) at
/// <c>Switching</c>, and waits for a release file so another process can be
/// started while the switch has provably not happened. A pause timeout is
/// reported as the distinct <c>pause_timeout</c> failure, never as a passing
/// restore. <c>--fault-rollback</c> drives the real coordinator through a
/// failed rollback into the persisted writes-blocked state.
/// </para>
/// </summary>
public static class RestoreScenario
{
    public static async Task<HarnessScenarioResult> RunAsync(
        HarnessOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Scenario != HarnessScenario.Restore)
        {
            throw new ArgumentException("RestoreScenario requires the restore scenario.", nameof(options));
        }

        var runId = C6HarnessPaths.ResolveRunId(options);
        var dataRoot = C6HarnessPaths.RequireUnderRunRoot(options.DataRoot, nameof(options.DataRoot));
        SingleInstanceGuard? instanceGuard = null;
        var activations = new int[1];
        try
        {
            if (options.SingleInstanceHold)
            {
                instanceGuard = new SingleInstanceGuard(
                    dataRoot,
                    new DiagnosticLog(Path.Combine(dataRoot, "logs")));
                var instanceResult = await instanceGuard
                    .StartAsync(TimeSpan.FromSeconds(3), cancellationToken)
                    .ConfigureAwait(false);
                if (instanceResult != SingleInstanceStartResult.Owner)
                {
                    return new HarnessScenarioResult
                    {
                        Scenario = "restore",
                        Status = "instance_not_owner",
                        Passed = false,
                        FailureStage = "instance",
                        ErrorCode = instanceResult.ToString(),
                        Instance = new InstanceEvidence
                        {
                            StartResult = instanceResult.ToString(),
                            ActivationCount = 0
                        },
                        Message = "The restore could not acquire single-instance ownership."
                    };
                }

                instanceGuard.ActivationRequested += () => Interlocked.Increment(ref activations[0]);
            }

            var outcome = await RunCoreAsync(options, runId, dataRoot, cancellationToken)
                .ConfigureAwait(false);
            return outcome.Result with
            {
                Pause = outcome.Pause,
                Instance = instanceGuard is null
                    ? null
                    : new InstanceEvidence
                    {
                        StartResult = SingleInstanceStartResult.Owner.ToString(),
                        ActivationCount = Volatile.Read(ref activations[0])
                    }
            };
        }
        finally
        {
            if (instanceGuard is not null)
            {
                await instanceGuard.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<RestoreOutcome> RunCoreAsync(
        HarnessOptions options,
        string runId,
        string dataRoot,
        CancellationToken cancellationToken)
    {
        var backupDirectory = C6HarnessPaths.RequireUnderRunRoot(
            options.BackupDirectory
                ?? throw new ArgumentException("--backup-dir is required for the restore scenario."),
            nameof(options.BackupDirectory));
        var checkpointDirectory = C6HarnessPaths.RequireUnderRunRoot(
            options.CheckpointDirectory
                ?? throw new ArgumentException("--checkpoint-dir is required for the restore scenario."),
            nameof(options.CheckpointDirectory));
        var pauseReleasePath = options.PauseAt is null
            ? null
            : C6HarnessPaths.RequireUnderRunRoot(
                options.PauseReleasePath
                    ?? Path.Combine(checkpointDirectory, $"{options.PauseAt}.release"),
                nameof(options.PauseReleasePath));

        await SaveScenario.EnsureSeededAsync(
            dataRoot,
            options.NoteCount,
            options.VisibleCount,
            runId,
            cancellationToken).ConfigureAwait(false);
        await C6HarnessState.WriteReadyAsync(
            dataRoot,
            schemaVersion: 2,
            runId,
            cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(backupDirectory);

        var databasePath = Path.Combine(dataRoot, "notes.db");
        var requestedPackage = string.IsNullOrWhiteSpace(options.PackagePath)
            ? null
            : C6HarnessPaths.RequireUnderRunRoot(
                options.PackagePath,
                nameof(options.PackagePath));
        var packagePath = await EnsurePackageAsync(
            requestedPackage,
            databasePath,
            backupDirectory,
            cancellationToken).ConfigureAwait(false);
        var sink = new CheckpointFileSink(
            checkpointDirectory,
            options.KillMarker,
            holdTimeout: TimeSpan.FromMinutes(2));
        var journalRecorder = new RestoreJournalRecorder(
            Path.Combine(dataRoot, "recovery-journal.json"),
            databasePath,
            dataRoot);

        IRestoreStore restoreStore = new SqliteRestoreStore();
        if (options.FaultPostSwitch || options.FaultRollback)
        {
            restoreStore = new PostSwitchVerificationFaultRestoreStore(restoreStore);
        }

        if (options.FaultRollback)
        {
            restoreStore = new FailingRollbackRestoreStore(restoreStore);
        }

        PausingRestoreStore? pauseStore = null;
        if (options.PauseAt is not null && pauseReleasePath is not null)
        {
            pauseStore = new PausingRestoreStore(
                restoreStore,
                sink,
                options.PauseAt,
                pauseReleasePath,
                TimeSpan.FromSeconds(options.PauseSeconds));
            restoreStore = pauseStore;
        }

        var checkpointingStore = new CheckpointingRestoreStore(
            restoreStore,
            sink,
            journalRecorder.RecordAsync);
        var draftStore = new FileNoteDraftStore(Path.Combine(dataRoot, "drafts"));
        var protection = new BackupCoordinator(
            new SqliteSnapshotProvider(databasePath),
            new ZipBackupPackageStore());
        // Production fidelity: the desktop host passes the persistent gate to
        // the restore coordinator, so a failed rollback blocks and persists the
        // write gate before the scenario below records the terminal journal.
        var writeGate = new PersistentDatabaseWriteGate(
            new ApplicationStateStore(Path.Combine(dataRoot, "application-state.json")),
            new DiagnosticLog(Path.Combine(dataRoot, "logs")));
        var coordinator = new RestoreCoordinator(
            new BackupPackageVerifier(),
            checkpointingStore,
            protection,
            draftStore,
            new DriveInfoDiskSpaceProvider(),
            writeGate,
            new SqliteSchemaMigrator());
        var request = new RestoreRequest
        {
            PackagePath = packagePath,
            CurrentDatabasePath = databasePath,
            BackupDirectory = backupDirectory,
            AllowPendingDrafts = true
        };

        RestoreResult result;
        try
        {
            result = await coordinator.RestoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await journalRecorder
                .RecordTerminalAsync(RestoreStage.Failed, protectionSnapshotPath: null, rollbackPath: null)
                .ConfigureAwait(false);
            // Fail-closed harness state for an exception that is not C6-M03
            // evidence: the restart probe must not observe a stale ready state.
            // The writes-blocked C6-M03 branch below deliberately never re-marks
            // state; it requires the production write gate to have persisted it.
            await C6HarnessState
                .MarkWritesBlockedAsync(dataRoot, "restore_exception", cancellationToken)
                .ConfigureAwait(false);
            var failed = await InspectAsync(databasePath, cancellationToken).ConfigureAwait(false);
            return new RestoreOutcome(new HarnessScenarioResult
            {
                Scenario = "restore",
                Status = "restore_exception",
                Passed = false,
                FailureStage = "restore",
                ErrorCode = exception.GetType().Name,
                DatabaseReadable = failed is not null,
                NoteCount = failed is null ? null : checked((int)failed.Counts.ActiveNotes),
                TotalNoteCount = failed is null ? null : Total(failed),
                Message = "The restore coordinator raised an unexpected exception."
            }, pauseStore?.Evidence);
        }

        var rollbackPath = result.Status == RestoreStatus.RollbackFailed
            ? TryFindLatestRollbackDatabase(dataRoot)
            : null;
        if (result.Status == RestoreStatus.Succeeded)
        {
            await journalRecorder
                .RecordTerminalAsync(RestoreStage.Completed, result.ProtectionSnapshotPath, rollbackPath: null)
                .ConfigureAwait(false);
        }
        else
        {
            await journalRecorder
                .RecordTerminalAsync(RestoreStage.Failed, result.ProtectionSnapshotPath, rollbackPath)
                .ConfigureAwait(false);
        }

        var restored = await InspectAsync(databasePath, cancellationToken).ConfigureAwait(false);
        var readable = restored is not null;
        var total = readable ? Total(restored!) : 0;
        // C6-M03 evidence belongs to the production persistent write gate.
        // Do not call C6HarnessState.MarkWritesBlockedAsync on this path: that
        // would turn the restart invariant into a harness self-assertion. The
        // state is read here and the scenario fails if the gate did not already
        // persist the block when the coordinator returned.
        var stateAfter = await C6HarnessState.ReadAsync(dataRoot, cancellationToken)
            .ConfigureAwait(false);
        var writeBlockPersisted = result.WritesBlocked && stateAfter?.WritesBlocked == true;
        var pauseTimedOut = pauseStore?.Evidence.TimedOut == true;
        var success = !pauseTimedOut
            && result.Status == RestoreStatus.Succeeded
            && readable
            && restored!.SchemaVersion == RestoreCoordinator.SupportedSchemaVersion
            && total > 0;
        var rollbackObserved = !pauseTimedOut
            && result.Status == RestoreStatus.SwitchFailed
            && result.RolledBack
            && readable
            && total > 0;
        var writesBlockedObserved = !pauseTimedOut
            && result.Status == RestoreStatus.RollbackFailed
            && result.WritesBlocked
            && writeBlockPersisted
            && readable
            && total > 0;
        var writeBlockMissing = result.WritesBlocked && !writeBlockPersisted;
        var passed = success || rollbackObserved || writesBlockedObserved;
        var pauseEvidence = pauseStore?.Evidence;
        return new RestoreOutcome(new HarnessScenarioResult
        {
            Scenario = "restore",
            Status = pauseTimedOut
                ? "pause_timeout"
                : success
                    ? "ok"
                    : rollbackObserved
                        ? "rollback_observed"
                        : writesBlockedObserved
                            ? "writes_blocked"
                            : writeBlockMissing
                                ? "write_gate_not_persisted"
                                : "unexpected_result",
            Passed = passed,
            FailureStage = passed
                ? null
                : pauseTimedOut
                    ? "pause"
                    : writeBlockMissing
                        ? "persistence"
                        : "restore",
            ErrorCode = pauseTimedOut
                ? "pause_timeout"
                : writeBlockMissing
                    ? "production_write_gate_not_persisted"
                    : result.Error?.Code.ToString(),
            RestoreStatus = result.Status,
            WritesBlocked = result.WritesBlocked,
            WriteBlockPersisted = result.WritesBlocked ? writeBlockPersisted : null,
            RolledBack = result.RolledBack,
            DatabaseReadable = readable,
            NoteCount = readable ? checked((int)restored!.Counts.ActiveNotes) : null,
            TotalNoteCount = readable ? total : null,
            Pause = pauseEvidence,
            Message = pauseTimedOut
                ? "The restore pause checkpoint timed out before release; the restore did not complete."
                : success
                    ? "The real restore switched and verified the package database."
                    : rollbackObserved
                        ? "The restore failed post-switch verification and the previous database was rolled back."
                        : writesBlockedObserved
                            ? "The restore rollback failed; writes are blocked and recovery evidence was retained."
                            : writeBlockMissing
                                ? "The restore reported writes blocked but the production write gate did not persist the block."
                                : "The restore did not match the expected safe outcome."
        }, pauseEvidence);
    }

    private static async Task<string> EnsurePackageAsync(
        string? packagePath,
        string databasePath,
        string backupDirectory,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(packagePath) && File.Exists(packagePath))
        {
            return Path.GetFullPath(packagePath);
        }

        var snapshotPath = Path.Combine(
            Path.GetDirectoryName(databasePath)!,
            $".c6-restore-package-{Guid.NewGuid():N}.db");
        try
        {
            await new SqliteSnapshotProvider(databasePath)
                .CreateAsync(snapshotPath, cancellationToken)
                .ConfigureAwait(false);
            var utcNow = DateTimeOffset.UtcNow;
            var published = await new ZipBackupPackageStore()
                .PublishAsync(
                    snapshotPath,
                    2,
                    new BackupRequest
                    {
                        BackupDirectory = backupDirectory,
                        Type = BackupType.Manual
                    },
                    utcNow,
                    TimeZoneInfo.ConvertTime(utcNow, TimeZoneInfo.Local),
                    cancellationToken)
                .ConfigureAwait(false);
            return published.PackagePath;
        }
        finally
        {
            try
            {
                if (File.Exists(snapshotPath))
                {
                    File.Delete(snapshotPath);
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

    private static async Task<RestoreDatabaseInfo?> InspectAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(databasePath))
        {
            return null;
        }

        try
        {
            return await new SqliteRestoreStore()
                .InspectAsync(databasePath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? TryFindLatestRollbackDatabase(string dataRoot)
    {
        try
        {
            var staging = Directory
                .GetDirectories(dataRoot, ".quicknotes-restore-*")
                .Select(path => new DirectoryInfo(path))
                .OrderByDescending(directory => directory.LastWriteTimeUtc)
                .FirstOrDefault();
            if (staging is null)
            {
                return null;
            }

            var rollback = Path.Combine(staging.FullName, "rollback.db");
            return File.Exists(rollback) ? rollback : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int Total(RestoreDatabaseInfo info) =>
        checked((int)(
            info.Counts.ActiveNotes + info.Counts.ArchivedNotes + info.Counts.TrashNotes));

    private sealed record RestoreOutcome(
        HarnessScenarioResult Result,
        RestorePauseEvidence? Pause);

    /// <summary>
    /// Holds the restore between the harness-written journal's <c>Switching</c>
    /// record and the actual database switch. The checkpoint marker is visible
    /// before the pause, and the release file ends it early. A missing release
    /// marks <see cref="RestorePauseEvidence.TimedOut"/> and throws so the
    /// scenario can report a distinct <c>pause_timeout</c> failure instead of a
    /// passing restore.
    /// </summary>
    private sealed class PausingRestoreStore : IRestoreStore
    {
        private readonly IRestoreStore _inner;
        private readonly CheckpointFileSink _checkpoints;
        private readonly string _checkpoint;
        private readonly string _releasePath;
        private readonly TimeSpan _timeout;
        private bool _markerWritten;
        private bool _released;
        private bool _timedOut;
        private long _waitMilliseconds;

        public PausingRestoreStore(
            IRestoreStore inner,
            CheckpointFileSink checkpoints,
            string checkpoint,
            string releasePath,
            TimeSpan timeout)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _checkpoints = checkpoints ?? throw new ArgumentNullException(nameof(checkpoints));
            _checkpoint = checkpoint;
            _releasePath = releasePath;
            _timeout = timeout;
            if (_timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }
        }

        public RestorePauseEvidence Evidence => new()
        {
            Checkpoint = _checkpoint,
            MarkerWritten = _markerWritten,
            Released = _released,
            TimedOut = _timedOut,
            WaitMilliseconds = Interlocked.Read(ref _waitMilliseconds)
        };

        public async Task SwitchAsync(
            string stagedDatabasePath,
            string currentDatabasePath,
            string rollbackDatabasePath,
            CancellationToken cancellationToken = default)
        {
            _checkpoints.Reached(_checkpoint);
            _markerWritten = true;
            var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            var deadline = DateTime.UtcNow + _timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(_releasePath))
                {
                    _released = true;
                    Interlocked.Exchange(
                        ref _waitMilliseconds,
                        (long)System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
                    await _inner.SwitchAsync(
                            stagedDatabasePath,
                            currentDatabasePath,
                            rollbackDatabasePath,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }

            _timedOut = true;
            Interlocked.Exchange(
                ref _waitMilliseconds,
                (long)System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            throw new TimeoutException(
                $"The pause checkpoint '{_checkpoint}' was not released within {_timeout}.");
        }

        public Task<RestoreDatabaseInfo> InspectAsync(
            string databasePath,
            CancellationToken cancellationToken = default) =>
            _inner.InspectAsync(databasePath, cancellationToken);

        public Task<string?> TryGetDatabaseHashAsync(
            string databasePath,
            CancellationToken cancellationToken = default) =>
            _inner.TryGetDatabaseHashAsync(databasePath, cancellationToken);

        public Task<RestoreDatabaseInfo> PrepareCurrentAsync(
            string currentDatabasePath,
            CancellationToken cancellationToken = default) =>
            _inner.PrepareCurrentAsync(currentDatabasePath, cancellationToken);

        public Task RollbackAsync(
            string rollbackDatabasePath,
            string currentDatabasePath,
            string failedDatabasePath,
            CancellationToken cancellationToken = default) =>
            _inner.RollbackAsync(
                rollbackDatabasePath,
                currentDatabasePath,
                failedDatabasePath,
                cancellationToken);
    }

    /// <summary>
    /// Harness-authored journal written in the production
    /// <see cref="RestoreJournal"/> format. The production coordinator does not
    /// write this file; the harness persists it so the desktop startup
    /// coordinator can observe a real interrupted-restore state after this
    /// process is killed.
    /// </summary>
    private sealed class RestoreJournalRecorder
    {
        private readonly FileRestoreJournalStore _store;
        private readonly Guid _restoreId = Guid.NewGuid();
        private readonly string _currentDatabasePath;
        private readonly string _stagingDirectory;
        private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;

        public RestoreJournalRecorder(
            string journalPath,
            string currentDatabasePath,
            string stagingDirectory)
        {
            _store = new FileRestoreJournalStore(journalPath);
            _currentDatabasePath = currentDatabasePath;
            _stagingDirectory = stagingDirectory;
        }

        public Task RecordAsync(RestoreStage stage) =>
            WriteAsync(stage, protectionSnapshotPath: null, rollbackPath: null);

        public Task RecordTerminalAsync(
            RestoreStage stage,
            string? protectionSnapshotPath,
            string? rollbackPath) =>
            WriteAsync(stage, protectionSnapshotPath, rollbackPath);

        private async Task WriteAsync(
            RestoreStage stage,
            string? protectionSnapshotPath,
            string? rollbackPath)
        {
            var now = DateTimeOffset.UtcNow;
            if (now < _startedAtUtc)
            {
                now = _startedAtUtc;
            }

            await _store.WriteAsync(new RestoreJournal
                {
                    FormatVersion = RestoreJournal.CurrentFormatVersion,
                    RestoreId = _restoreId,
                    Stage = stage,
                    CurrentDatabasePath = _currentDatabasePath,
                    StagingDirectory = _stagingDirectory,
                    ProtectionSnapshotPath = protectionSnapshotPath,
                    RollbackPath = rollbackPath,
                    StartedAtUtc = _startedAtUtc,
                    UpdatedAtUtc = now
                })
                .ConfigureAwait(false);
        }
    }
}
