using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuickNotes.Application;
using QuickNotes.Desktop.Editing;
using QuickNotes.Desktop.Hosting;
using QuickNotes.Domain;
using QuickNotes.Infrastructure.Backup;
using QuickNotes.Infrastructure.Drafts;
using QuickNotes.Infrastructure.Sqlite;

namespace QuickNotes.C6.Harness;

/// <summary>
/// Real service-level performance collection for C6 Task 5. It seeds only
/// synthetic data under <c>%TEMP%\QuickNotes-C6\&lt;runId&gt;</c>, runs the
/// production host, save coordinator, editor session, backup coordinator, and
/// restore coordinator, and emits timings plus process resources. WPF window
/// metrics are collected separately by <c>QuickNotes.C6.UiProbe</c> because a
/// real desktop window must never be created by the default test suite.
/// </summary>
public static class PerfScenario
{
    private const int SaveIterations = 50;
    private const int InputIterations = 100;

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static async Task<PerfScenarioResult> RunAsync(
        HarnessOptions options,
        bool budgetConfirmed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Scenario != HarnessScenario.Perf)
        {
            throw new ArgumentException("PerfScenario requires the perf scenario.", nameof(options));
        }

        var runId = C6HarnessPaths.ResolveRunId(options);
        var dataRoot = C6HarnessPaths.RequireUnderRunRoot(options.DataRoot, nameof(options.DataRoot));
        var metrics = new List<MetricSample>();
        var restoreChecks = new List<PerfRestoreCheck>();
        var stage = "seed";

        try
        {
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

            stage = "startup";
            metrics.Add(await MeasureStartupAsync(
                dataRoot,
                options.Iterations,
                cancellationToken).ConfigureAwait(false));

            var databasePath = Path.Combine(dataRoot, "notes.db");
            var repository = new SqliteNoteRepository(new SqliteConnectionFactory(databasePath));
            var draftStore = new FileNoteDraftStore(Path.Combine(dataRoot, "drafts"));
            var saves = new NoteSaveCoordinator(repository, draftStore);
            var noteId = await GetFirstActiveNoteIdAsync(repository, cancellationToken)
                .ConfigureAwait(false);

            stage = "save";
            metrics.Add(await MeasureSavesAsync(
                repository,
                saves,
                noteId,
                runId,
                cancellationToken).ConfigureAwait(false));

            stage = "input";
            metrics.Add(await MeasureInputAsync(
                repository,
                saves,
                draftStore,
                noteId,
                cancellationToken).ConfigureAwait(false));

            stage = "scale";
            foreach (var size in SelectScaleSizes(options.NoteCount))
            {
                var scale = await MeasureBackupAndRestoreAsync(
                    dataRoot,
                    size,
                    runId,
                    cancellationToken).ConfigureAwait(false);
                metrics.AddRange(scale.Metrics);
                restoreChecks.Add(scale.RestoreCheck);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return BuildResult(
                options,
                budgetConfirmed,
                metrics,
                restoreChecks,
                status: "failed",
                failureStage: stage,
                errorCode: exception.GetType().Name,
                message: "The perf probe failed before completing every measurement.");
        }

        return BuildResult(
            options,
            budgetConfirmed,
            metrics,
            restoreChecks,
            status: "ok",
            failureStage: null,
            errorCode: null,
            message: "The service-level perf probe completed against synthetic C6 data.");
    }

    private static async Task<MetricSample> MeasureStartupAsync(
        string dataRoot,
        int iterations,
        CancellationToken cancellationToken)
    {
        var samples = new List<MetricSample>(iterations);
        for (var index = 0; index < iterations; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            samples.Add(await MetricsCollector.SampleAsync(
                "startup_host",
                async token =>
                {
                    await using var host = new DesktopApplicationHost(
                        new DesktopStartupOptions { DataRoot = dataRoot });
                    var started = await host.StartAsync(token).ConfigureAwait(false);
                    if (!started.CanShowWindow
                        || started.Assessment?.CanEnterNormalUse != true)
                    {
                        throw new InvalidOperationException(
                            $"Startup mode was {started.Assessment?.Mode}.");
                    }
                },
                cancellationToken).ConfigureAwait(false));
        }

        return MetricsCollector.Aggregate("startup_host", samples);
    }

    private static async Task<Guid> GetFirstActiveNoteIdAsync(
        INoteRepository repository,
        CancellationToken cancellationToken)
    {
        var active = await repository.ListAsync(NoteLifecycle.Active, cancellationToken)
            .ConfigureAwait(false);
        return active.Count > 0
            ? active[0].Id
            : throw new InvalidOperationException("The perf data root has no active note.");
    }

    private static async Task<MetricSample> MeasureSavesAsync(
        INoteRepository repository,
        NoteSaveCoordinator saves,
        Guid noteId,
        string runId,
        CancellationToken cancellationToken)
    {
        var samples = new List<MetricSample>(SaveIterations);
        for (var index = 0; index < SaveIterations; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await repository.GetAsync(noteId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The perf note disappeared.");
            var tagIds = await repository.GetTagIdsAsync(noteId, cancellationToken)
                .ConfigureAwait(false);
            var updated = current with
            {
                Body = $"{C6DataFactory.SyntheticBodyMarker} run={runId} perf save {index:D4}",
                ModifiedAtUtc = DateTimeOffset.UtcNow
            };
            var draftId = Guid.NewGuid();
            samples.Add(await MetricsCollector.SampleAsync(
                "save",
                async token =>
                {
                    var result = await saves.UpdateAsync(
                            updated,
                            current.Version,
                            tagIds,
                            draftId,
                            token)
                        .ConfigureAwait(false);
                    if (result.Status != SaveStatus.Saved)
                    {
                        throw new InvalidOperationException(
                            $"Save status was {result.Status}.");
                    }
                },
                cancellationToken).ConfigureAwait(false));
        }

        return MetricsCollector.Aggregate("save", samples);
    }

    private static async Task<MetricSample> MeasureInputAsync(
        INoteRepository repository,
        NoteSaveCoordinator saves,
        INoteDraftStore draftStore,
        Guid noteId,
        CancellationToken cancellationToken)
    {
        // Autosave is pushed far into the future so this probe measures the
        // synchronous editor input path (state mutation + status event), not
        // a background draft/save debounce that the UI does not wait for.
        var workspace = new EditorWorkspaceService(
            repository,
            saves,
            draftStore,
            new EditorAutosaveOptions
            {
                DraftDebounce = TimeSpan.FromHours(1),
                DraftMaxDelay = TimeSpan.FromHours(2),
                SaveDebounce = TimeSpan.FromHours(1),
                SaveMaxDelay = TimeSpan.FromHours(2)
            });
        var session = await workspace.OpenAsync(noteId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The perf editor session could not be opened.");
        try
        {
            var samples = new List<MetricSample>(InputIterations);
            for (var index = 0; index < InputIterations; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var text = $"perf input {index:D4}";
                samples.Add(await MetricsCollector.SampleAsync(
                    "input",
                    _ =>
                    {
                        session.ChangeText(null, text);
                        return Task.CompletedTask;
                    },
                    cancellationToken).ConfigureAwait(false));
            }

            return MetricsCollector.Aggregate("input", samples);
        }
        finally
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<int> SelectScaleSizes(int noteCount)
    {
        // The acceptance run measures 100 and 1,000 note databases. Small
        // contract tests may seed fewer notes; never invent a larger database
        // than the run asked for.
        if (noteCount >= 100)
        {
            return noteCount == 100 ? new[] { 100 } : new[] { 100, noteCount };
        }

        return new[] { noteCount };
    }

    private static async Task<ScaleMeasurement> MeasureBackupAndRestoreAsync(
        string dataRoot,
        int noteCount,
        string runId,
        CancellationToken cancellationToken)
    {
        var scaleRoot = Path.Combine(dataRoot, "scale", $"n{noteCount}");
        var visible = Math.Min(C6Budget.VisibleWindows, noteCount);
        await SaveScenario.EnsureSeededAsync(
            scaleRoot,
            noteCount,
            visible,
            runId,
            cancellationToken).ConfigureAwait(false);
        var backupDirectory = Path.Combine(scaleRoot, "backups");
        Directory.CreateDirectory(backupDirectory);
        var databasePath = Path.Combine(scaleRoot, "notes.db");

        var snapshot = new TimingSnapshotProvider(new SqliteSnapshotProvider(databasePath));
        var backups = new BackupCoordinator(snapshot, new ZipBackupPackageStore());
        string packagePath = string.Empty;
        var backupSample = await MetricsCollector.SampleAsync(
            $"backup_{noteCount}_complete",
            async token =>
            {
                var result = await backups.CreateAsync(
                        new BackupRequest
                        {
                            BackupDirectory = backupDirectory,
                            Type = BackupType.Manual
                        },
                        token)
                    .ConfigureAwait(false);
                if (result.Status != BackupStatus.Succeeded
                    || string.IsNullOrWhiteSpace(result.PackagePath))
                {
                    throw new InvalidOperationException(
                        $"Backup status was {result.Status}.");
                }

                packagePath = result.PackagePath;
            },
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            throw new InvalidOperationException("The backup produced no package path.");
        }

        var backupDispatch = MetricsCollector.FromMilliseconds(
            $"backup_{noteCount}_dispatch",
            new[] { snapshot.SnapshotStartMs ?? backupSample.P50Ms });

        var restore = new RestoreCoordinator(
            new BackupPackageVerifier(),
            new SqliteRestoreStore(),
            backups,
            new FileNoteDraftStore(Path.Combine(scaleRoot, "drafts")),
            new DriveInfoDiskSpaceProvider(),
            new DatabaseWriteGate(),
            new SqliteSchemaMigrator());
        var progress = new ProgressRecorder();
        var restoreSample = await MetricsCollector.SampleAsync(
            $"restore_{noteCount}_complete",
            async token =>
            {
                var result = await restore.RestoreAsync(
                        new RestoreRequest
                        {
                            PackagePath = packagePath,
                            CurrentDatabasePath = databasePath,
                            BackupDirectory = backupDirectory
                        },
                        progress,
                        token)
                    .ConfigureAwait(false);
                if (result.Status != RestoreStatus.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Restore status was {result.Status}.");
                }
            },
            cancellationToken).ConfigureAwait(false);
        var firstProgress = MetricsCollector.FromMilliseconds(
            $"restore_{noteCount}_first_progress",
            new[] { progress.FirstProgressMs ?? restoreSample.P50Ms });

        var restored = await new SqliteRestoreStore()
            .InspectAsync(databasePath, cancellationToken)
            .ConfigureAwait(false);
        var restoredCount = checked((int)(
            restored.Counts.ActiveNotes
            + restored.Counts.ArchivedNotes
            + restored.Counts.TrashNotes));
        var passed = restoredCount == noteCount;
        return new ScaleMeasurement(
            new[] { backupSample, backupDispatch, restoreSample, firstProgress },
            new PerfRestoreCheck
            {
                NoteCount = noteCount,
                BackupStatus = BackupStatus.Succeeded.ToString(),
                RestoreStatus = RestoreStatus.Succeeded.ToString(),
                RestoredNoteCount = restoredCount,
                DurationMs = restoreSample.P50Ms,
                Passed = passed,
                Message = passed
                    ? "The restored database contains the expected note count."
                    : "The restored database note count did not match the seed."
            });
    }

    private static PerfScenarioResult BuildResult(
        HarnessOptions options,
        bool budgetConfirmed,
        IReadOnlyList<MetricSample> metrics,
        IReadOnlyList<PerfRestoreCheck> restoreChecks,
        string status,
        string? failureStage,
        string? errorCode,
        string? message)
    {
        var inputs = new List<BudgetCheckInput>();
        if (status == "ok")
        {
            AddP95Check(inputs, metrics, "startup_host", C6Budget.StartupReadyP95.TotalMilliseconds);
            AddP95Check(inputs, metrics, "save", C6Budget.SaveP95.TotalMilliseconds);
            AddP99Check(inputs, metrics, "save", C6Budget.SaveP99.TotalMilliseconds);
            AddP95Check(inputs, metrics, "input", C6Budget.InputP95.TotalMilliseconds);
        }

        var verdict = C6PerformanceGate.Evaluate(budgetConfirmed, inputs);
        var passed = status == "ok"
            && restoreChecks.All(check => check.Passed)
            && verdict.PerformanceStatus != C6PerformanceGate.Fail;
        var result = new PerfScenarioResult
        {
            Status = status,
            Passed = passed,
            BudgetStatus = verdict.BudgetStatus,
            PerformanceStatus = status == "ok"
                ? verdict.PerformanceStatus
                : C6PerformanceGate.Fail,
            NoteCount = options.NoteCount,
            VisibleCount = options.VisibleCount,
            Iterations = options.Iterations,
            Metrics = metrics,
            BudgetChecks = verdict.Checks,
            Budget = new PerfBudgetEvidence
            {
                ConfirmationEnvironmentVariable = C6Budget.ConfirmationEnvironmentVariable,
                ApprovalStatus = C6Budget.ApprovalStatus,
                StartupReadyP95Ms = C6Budget.StartupReadyP95.TotalMilliseconds,
                StartupWithUiP95Ms = C6Budget.StartupWithUiP95.TotalMilliseconds,
                SaveP95Ms = C6Budget.SaveP95.TotalMilliseconds,
                SaveP99Ms = C6Budget.SaveP99.TotalMilliseconds,
                InputP95Ms = C6Budget.InputP95.TotalMilliseconds,
                WorkingSetBytes = C6Budget.WorkingSetBytes,
                HistoricalNotes = C6Budget.HistoricalNotes,
                VisibleWindows = C6Budget.VisibleWindows
            },
            RestoreChecks = restoreChecks,
            FailureStage = failureStage,
            ErrorCode = errorCode,
            Message = message
        };

        if (!string.IsNullOrWhiteSpace(options.OutputPath))
        {
            WriteOutput(options.OutputPath, result);
        }

        return result;
    }

    private static void AddP95Check(
        ICollection<BudgetCheckInput> inputs,
        IReadOnlyList<MetricSample> metrics,
        string metricName,
        double limit)
    {
        var metric = metrics.FirstOrDefault(item => item.Name == metricName);
        if (metric is null || !metric.HasSamples)
        {
            // A missing measurement is never silently skipped: it produces a
            // check that cannot pass under a confirmed gate. Use the finite
            // error sentinel so the result stays JSON-serializable.
            inputs.Add(new BudgetCheckInput(
                $"{metricName}_p95",
                MetricsCollector.ErrorDurationMs,
                limit,
                "ms"));
            return;
        }

        inputs.Add(new BudgetCheckInput(
            $"{metricName}_p95",
            metric.P95Ms,
            limit,
            "ms"));
    }

    private static void AddP99Check(
        ICollection<BudgetCheckInput> inputs,
        IReadOnlyList<MetricSample> metrics,
        string metricName,
        double limit)
    {
        var metric = metrics.FirstOrDefault(item => item.Name == metricName);
        inputs.Add(new BudgetCheckInput(
            $"{metricName}_p99",
            metric is { HasSamples: true } ? metric.P99Ms : MetricsCollector.ErrorDurationMs,
            limit,
            "ms"));
    }

    private static void WriteOutput(string outputPath, PerfScenarioResult result)
    {
        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(result, SerializerOptions));
        File.Move(temporary, fullPath, overwrite: true);
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed record ScaleMeasurement(
        IReadOnlyList<MetricSample> Metrics,
        PerfRestoreCheck RestoreCheck);

    /// <summary>
    /// Records the beginning of the first snapshot of a backup so the report
    /// can separate dispatch latency from full background completion.
    /// </summary>
    private sealed class TimingSnapshotProvider : IBackupSnapshotProvider
    {
        private readonly IBackupSnapshotProvider _inner;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        public TimingSnapshotProvider(IBackupSnapshotProvider inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public double? SnapshotStartMs { get; private set; }

        public Task<SnapshotInfo> CreateAsync(
            string destinationDatabasePath,
            CancellationToken cancellationToken = default)
        {
            SnapshotStartMs ??= _stopwatch.Elapsed.TotalMilliseconds;
            return _inner.CreateAsync(destinationDatabasePath, cancellationToken);
        }
    }

    /// <summary>
    /// Synchronous progress recorder. The restore coordinator reports progress
    /// inline; using a custom recorder avoids the thread-pool hop of
    /// <see cref="Progress{T}"/> skewing the UI-response measurement.
    /// </summary>
    private sealed class ProgressRecorder : IProgress<RestoreProgress>
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        public double? FirstProgressMs { get; private set; }

        public void Report(RestoreProgress value) =>
            FirstProgressMs ??= _stopwatch.Elapsed.TotalMilliseconds;
    }
}
