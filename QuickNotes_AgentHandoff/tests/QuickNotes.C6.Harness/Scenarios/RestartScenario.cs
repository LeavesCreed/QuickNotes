using Microsoft.Data.Sqlite;
using QuickNotes.Application;
using QuickNotes.Desktop.Diagnostics;
using QuickNotes.Desktop.Migration;
using QuickNotes.Desktop.Paths;
using QuickNotes.Desktop.Recovery;
using QuickNotes.Desktop.Startup;
using QuickNotes.Desktop.State;
using QuickNotes.Infrastructure.Backup;
using QuickNotes.Infrastructure.Drafts;
using QuickNotes.Infrastructure.Sqlite;

namespace QuickNotes.C6.Harness;

/// <summary>
/// Startup/restart probe used after every process-kill case. It runs the real
/// desktop <see cref="StartupCoordinator"/> against a disposable C6 data root
/// and then reports only counts, startup mode, write-block state, and recovery
/// artifact kinds/names. Note bodies and machine paths are never emitted.
///
/// The probe never initializes a missing data root: if both the state file and
/// the database are absent it reports <c>NoData</c> and fails, so a kill that
/// destroyed the data cannot be mistaken for first-run success.
/// </summary>
public static class RestartScenario
{
    public static async Task<HarnessScenarioResult> RunAsync(
        HarnessOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Scenario != HarnessScenario.Restart)
        {
            throw new ArgumentException("RestartScenario requires the restart scenario.", nameof(options));
        }

        var runId = C6HarnessPaths.ResolveRunId(options);
        var dataRoot = C6HarnessPaths.RequireUnderRunRoot(options.DataRoot, nameof(options.DataRoot));
        var backupDirectory = string.IsNullOrWhiteSpace(options.BackupDirectory)
            ? null
            : C6HarnessPaths.RequireUnderRunRoot(
                options.BackupDirectory,
                nameof(options.BackupDirectory));
        var paths = DesktopPaths.Create(dataRoot);
        var databasePath = paths.DatabasePath;

        var beforeInfo = await TryInspectAsync(databasePath, cancellationToken).ConfigureAwait(false);
        var beforeSchema = beforeInfo?.SchemaVersion;
        var stateBefore = await C6HarnessState.ReadAsync(dataRoot, cancellationToken)
            .ConfigureAwait(false);
        var databaseExists = File.Exists(databasePath);
        var draftCountBefore = await CountDraftsAsync(paths.DraftDirectory, cancellationToken)
            .ConfigureAwait(false);

        StartupAssessment? assessment = null;
        if (databaseExists || stateBefore is not null)
        {
            var restoreStore = new SqliteRestoreStore();
            var log = new DiagnosticLog(paths.LogDirectory);
            var journalStore = new FileRestoreJournalStore(paths.RestoreJournalPath);
            var migrationService = new SchemaMigrationService(
                paths,
                new SqliteSchemaMigrator(),
                restoreStore,
                log);
            var coordinator = new StartupCoordinator(
                paths,
                new ApplicationStateStore(paths.StatePath),
                journalStore,
                new StartupArtifactScanner(paths, backupDirectory),
                restoreStore,
                migrationService,
                token => new SqliteSchema(new SqliteConnectionFactory(paths.DatabasePath))
                    .InitializeAsync(token),
                log);
            assessment = await coordinator.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        var afterInfo = await TryInspectAsync(databasePath, cancellationToken).ConfigureAwait(false);
        var stateAfter = await C6HarnessState.ReadAsync(dataRoot, cancellationToken)
            .ConfigureAwait(false);
        var draftCountAfter = await CountDraftsAsync(paths.DraftDirectory, cancellationToken)
            .ConfigureAwait(false);
        var maxNoteVersion = await TryReadMaxNoteVersionAsync(databasePath, cancellationToken)
            .ConfigureAwait(false);
        var targetNote = await TryReadTargetNoteAsync(
                databasePath,
                options.ProbeNoteId,
                cancellationToken)
            .ConfigureAwait(false);
        var journal = await new FileRestoreJournalStore(paths.RestoreJournalPath)
            .ReadAsync(cancellationToken)
            .ConfigureAwait(false);
        var artifacts = MapArtifacts(assessment?.Artifacts);
        var packages = await VerifyBackupPackagesAsync(backupDirectory, cancellationToken)
            .ConfigureAwait(false);

        var startupMode = assessment?.Mode.ToString()
            ?? (databaseExists || stateBefore is not null ? "Unknown" : "NoData");
        var noteCount = checked((int)(afterInfo?.Counts.ActiveNotes ?? 0));
        var total = afterInfo is null
            ? 0
            : checked((int)(
                afterInfo.Counts.ActiveNotes
                + afterInfo.Counts.ArchivedNotes
                + afterInfo.Counts.TrashNotes));
        var readable = afterInfo is not null;
        var writesBlocked = assessment?.Mode == StartupMode.WriteBlocked
            || stateAfter?.WritesBlocked == true;
        var modeAccepted = assessment is not null
            && assessment.Mode is StartupMode.Ready
                or StartupMode.FirstRunInitialized
                or StartupMode.NeedsRecovery
                or StartupMode.WriteBlocked;
        var passed = databaseExists
            && readable
            && stateBefore is not null
            && modeAccepted
            && total > 0;
        var probe = new RestartProbeEvidence
        {
            StartupMode = startupMode,
            StartupReason = assessment?.Reason.ToString(),
            ErrorCode = assessment?.ErrorCode,
            WritesBlocked = writesBlocked,
            DatabaseExists = databaseExists,
            DatabaseReadable = readable,
            SchemaVersionBeforeStartup = beforeSchema,
            SchemaVersionAfterStartup = afterInfo?.SchemaVersion,
            NoteCount = noteCount,
            TotalNoteCount = total,
            MaxNoteVersion = maxNoteVersion,
            TargetNoteId = options.ProbeNoteId,
            TargetNoteExists = options.ProbeNoteId is null ? null : targetNote is not null,
            TargetNoteVersion = targetNote?.Version,
            DraftCountBeforeStartup = draftCountBefore,
            DraftCountAfterStartup = draftCountAfter,
            DraftCount = draftCountAfter,
            RestoreJournalStage = journal.Journal?.Stage.ToString(),
            RecoveryArtifacts = artifacts,
            BackupPackages = packages
        };
        return new HarnessScenarioResult
        {
            Scenario = "restart",
            Status = passed ? "ok" : "failed",
            Passed = passed,
            FailureStage = passed ? null : "restart",
            ErrorCode = assessment?.ErrorCode,
            DatabaseReadable = readable,
            NoteCount = noteCount,
            TotalNoteCount = total,
            RestartProbe = probe,
            Message = passed
                ? "The restart probe completed against the real startup coordinator."
                : assessment?.Message ?? "The restart probe found no usable state or database."
        };
    }

    private static async Task<RestoreDatabaseInfo?> TryInspectAsync(
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

    private static async Task<int> CountDraftsAsync(
        string draftDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            var drafts = await new FileNoteDraftStore(draftDirectory)
                .ListAsync(noteId: null, cancellationToken)
                .ConfigureAwait(false);
            return drafts.Count;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static async Task<long?> TryReadMaxNoteVersionAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(databasePath))
        {
            return null;
        }

        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = 1
            }.ToString();
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM notes;";
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
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

    private static async Task<QuickNotes.Domain.Note?> TryReadTargetNoteAsync(
        string databasePath,
        Guid? noteId,
        CancellationToken cancellationToken)
    {
        if (noteId is null || !File.Exists(databasePath))
        {
            return null;
        }

        try
        {
            return await new SqliteNoteRepository(new SqliteConnectionFactory(databasePath))
                .GetAsync(noteId.Value, cancellationToken)
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

    private static IReadOnlyList<RecoveryArtifactEvidence> MapArtifacts(
        StartupArtifactInventory? inventory)
    {
        if (inventory is null)
        {
            return Array.Empty<RecoveryArtifactEvidence>();
        }

        var mapped = new List<RecoveryArtifactEvidence>(inventory.Artifacts.Count);
        foreach (var artifact in inventory.Artifacts)
        {
            mapped.Add(new RecoveryArtifactEvidence(
                artifact.Kind.ToString(),
                Path.GetFileName(artifact.Path.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar)),
                artifact.IsDirectory));
        }

        return mapped;
    }

    private static async Task<IReadOnlyList<BackupPackageEvidence>> VerifyBackupPackagesAsync(
        string? backupDirectory,
        CancellationToken cancellationToken)
    {
        if (backupDirectory is null || !Directory.Exists(backupDirectory))
        {
            return Array.Empty<BackupPackageEvidence>();
        }

        var verifier = new BackupPackageVerifier();
        var packages = new List<BackupPackageEvidence>();
        foreach (var path in Directory.EnumerateFiles(backupDirectory, "quicknotes-*.zip"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var valid = false;
            try
            {
                await verifier.VerifyAsync(path, backupDirectory, cancellationToken)
                    .ConfigureAwait(false);
                valid = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                valid = false;
            }

            packages.Add(new BackupPackageEvidence(
                Path.GetFileName(path),
                valid,
                new FileInfo(path).Length));
        }

        packages.Sort((left, right) => string.Compare(
            left.FileName,
            right.FileName,
            StringComparison.OrdinalIgnoreCase));
        return packages;
    }
}
