using QuickNotes.Application;
using QuickNotes.Desktop.Recovery;
using QuickNotes.Infrastructure.Backup;
using QuickNotes.Infrastructure.Drafts;
using QuickNotes.Infrastructure.Sqlite;

namespace QuickNotes.C6.Harness;

/// <summary>
/// Real restore scenario used by C6-K05/K06/K07. The extraction, protection
/// snapshot, migration, switch, and rollback all run through the production
/// restore services. Only the checkpoint markers, the restore journal writer
/// (the desktop host already reads this exact journal format at startup), and
/// the optional post-switch verification fault are C6 test composition.
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
        var backupDirectory = C6HarnessPaths.RequireUnderRunRoot(
            options.BackupDirectory
                ?? throw new ArgumentException("--backup-dir is required for the restore scenario."),
            nameof(options.BackupDirectory));
        var checkpointDirectory = C6HarnessPaths.RequireUnderRunRoot(
            options.CheckpointDirectory
                ?? throw new ArgumentException("--checkpoint-dir is required for the restore scenario."),
            nameof(options.CheckpointDirectory));

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
        if (options.FaultPostSwitch)
        {
            restoreStore = new PostSwitchVerificationFaultRestoreStore(restoreStore);
        }

        var checkpointingStore = new CheckpointingRestoreStore(
            restoreStore,
            sink,
            journalRecorder.RecordAsync);
        var draftStore = new FileNoteDraftStore(Path.Combine(dataRoot, "drafts"));
        var protection = new BackupCoordinator(
            new SqliteSnapshotProvider(databasePath),
            new ZipBackupPackageStore());
        var writeGate = new DatabaseWriteGate();
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
                .RecordTerminalAsync(RestoreStage.Failed, protectionSnapshotPath: null)
                .ConfigureAwait(false);
            await C6HarnessState
                .MarkWritesBlockedAsync(dataRoot, "restore_exception", cancellationToken)
                .ConfigureAwait(false);
            var failed = await InspectAsync(databasePath, cancellationToken).ConfigureAwait(false);
            return new HarnessScenarioResult
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
            };
        }

        if (result.Status == RestoreStatus.Succeeded)
        {
            await journalRecorder
                .RecordTerminalAsync(RestoreStage.Completed, result.ProtectionSnapshotPath)
                .ConfigureAwait(false);
        }
        else
        {
            await journalRecorder
                .RecordTerminalAsync(RestoreStage.Failed, result.ProtectionSnapshotPath)
                .ConfigureAwait(false);
        }

        if (result.WritesBlocked)
        {
            await C6HarnessState
                .MarkWritesBlockedAsync(dataRoot, "restore_rollback_failed", cancellationToken)
                .ConfigureAwait(false);
        }

        var restored = await InspectAsync(databasePath, cancellationToken).ConfigureAwait(false);
        var readable = restored is not null;
        var pass = result.Status == RestoreStatus.Succeeded
            && readable
            && restored!.SchemaVersion == RestoreCoordinator.SupportedSchemaVersion
            && Total(restored) > 0;
        var rollbackObserved = result.Status == RestoreStatus.SwitchFailed
            && result.RolledBack
            && readable
            && Total(restored!) > 0;
        return new HarnessScenarioResult
        {
            Scenario = "restore",
            Status = pass
                ? "ok"
                : rollbackObserved
                    ? "rollback_observed"
                    : "unexpected_result",
            Passed = pass || rollbackObserved,
            FailureStage = pass || rollbackObserved ? null : "restore",
            ErrorCode = result.Error?.Code.ToString(),
            DatabaseReadable = readable,
            NoteCount = readable ? checked((int)restored!.Counts.ActiveNotes) : null,
            TotalNoteCount = readable ? Total(restored!) : null,
            Message = pass
                ? "The real restore switched and verified the package database."
                : rollbackObserved
                    ? "The restore failed post-switch verification and the previous database was rolled back."
                    : "The restore did not match the expected successful outcome."
        };
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

    private static int Total(RestoreDatabaseInfo info) =>
        checked((int)(
            info.Counts.ActiveNotes + info.Counts.ArchivedNotes + info.Counts.TrashNotes));

    /// <summary>
    /// Maintains a journal in the production <see cref="RestoreJournal"/>
    /// format. The desktop startup coordinator already reads this file; writing
    /// it here lets the restart probe observe a real interrupted-restore state
    /// after the harness process is killed.
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
            WriteAsync(stage, protectionSnapshotPath: null);

        public Task RecordTerminalAsync(RestoreStage stage, string? protectionSnapshotPath) =>
            WriteAsync(stage, protectionSnapshotPath);

        private async Task WriteAsync(RestoreStage stage, string? protectionSnapshotPath)
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
                    StartedAtUtc = _startedAtUtc,
                    UpdatedAtUtc = now
                })
                .ConfigureAwait(false);
        }
    }
}
