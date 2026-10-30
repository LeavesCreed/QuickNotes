using System.Security.Cryptography;
using QuickNotes.Application;
using QuickNotes.C6.Harness;
using QuickNotes.Desktop.Editing;
using QuickNotes.Desktop.Hosting;
using QuickNotes.Desktop.Paths;
using QuickNotes.Desktop.Recovery;
using QuickNotes.Desktop.Startup;
using QuickNotes.Desktop.State;
using QuickNotes.Infrastructure.Backup;
using QuickNotes.Infrastructure.Drafts;
using QuickNotes.Infrastructure.Sqlite;

namespace QuickNotes.IntegrationTests.C6;

/// <summary>
/// Task 4 cross-restart invariants for the failed-rollback path (C6-M03,
/// C6-M04 and C6-M05). The fixture drives the real restore coordinator through
/// <c>RestoreScenario --fault-rollback</c> into the production
/// <see cref="RestoreStatus.RollbackFailed"/> / writes-blocked state, then a
/// fresh <see cref="DesktopApplicationHost"/> performs the restart. Everything
/// lives under <c>%TEMP%\QuickNotes-C6</c>; no production data root, HKCU
/// value, tray, or hotkey is touched.
/// </summary>
public sealed class C6RecoveryInvariantTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(3);

    [Fact]
    public async Task RestartAfterFailedRollback_RemainsWriteBlocked()
    {
        await using var fixture = await C6RecoveryFixture.CreateBlockedAsync();
        await using var host = new DesktopApplicationHost(fixture.Options);
        var result = await host.StartAsync();

        Assert.Equal(StartupMode.WriteBlocked, result.Assessment!.Mode);
        Assert.True(result.Assessment.State!.WritesBlocked);
        Assert.Equal(StartupReason.WritesBlocked, result.Assessment.Reason);
        Assert.True(result.CanShowWindow);
        Assert.True(host.WriteGate!.IsBlocked);
        Assert.Null(host.EditingServices);
        Assert.Null(host.BackupServices);
        await Assert.ThrowsAsync<DatabaseWriteBlockedException>(
            () => host.WriteGate.EnterWriteAsync());
    }

    [Fact]
    public async Task RestartAfterFailedRollback_RetainsProtectionRollbackAndFailedJournal()
    {
        await using var fixture = await C6RecoveryFixture.CreateBlockedAsync();
        await using var host = new DesktopApplicationHost(fixture.Options);
        var started = await host.StartAsync();

        var journal = await new FileRestoreJournalStore(fixture.JournalPath).ReadAsync();
        Assert.Equal(RestoreJournalReadStatus.Found, journal.Status);
        Assert.Equal(RestoreStage.Failed, journal.Journal!.Stage);
        Assert.True(journal.Journal.IsTerminal);
        Assert.Equal(
            Path.GetFullPath(fixture.ProtectionSnapshotPath),
            Path.GetFullPath(journal.Journal.ProtectionSnapshotPath!));
        Assert.NotNull(journal.Journal.RollbackPath);
        Assert.True(File.Exists(journal.Journal.RollbackPath));
        Assert.True(File.Exists(fixture.ProtectionSnapshotPath));

        var protectionManifest = await new BackupPackageVerifier().VerifyAsync(
            fixture.ProtectionSnapshotPath,
            Path.GetDirectoryName(fixture.ProtectionSnapshotPath)!);
        Assert.Equal(2, protectionManifest.SchemaVersion);

        var rollbackInfo = await new SqliteRestoreStore().InspectAsync(
            journal.Journal.RollbackPath!);
        Assert.Equal(2, rollbackInfo.SchemaVersion);
        Assert.Equal(
            fixture.OriginalNoteCount,
            rollbackInfo.Counts.ActiveNotes
            + rollbackInfo.Counts.ArchivedNotes
            + rollbackInfo.Counts.TrashNotes);

        var currentInfo = await new SqliteRestoreStore().InspectAsync(fixture.DatabasePath);
        Assert.Equal(2, currentInfo.SchemaVersion);
        Assert.Equal(
            fixture.PackageNoteCount,
            currentInfo.Counts.ActiveNotes
            + currentInfo.Counts.ArchivedNotes
            + currentInfo.Counts.TrashNotes);

        var verification = await C6RecoveryVerifier.VerifyAsync(
            fixture.DataRoot,
            started.Assessment!,
            fixture.CreateBlockedExpectation());
        Assert.True(verification.IsValid, verification.Describe());

        // C6-M05: startup must not delete protection, rollback, staging,
        // journal, or draft evidence while it refuses normal use.
        foreach (var artifact in fixture.RetainedArtifacts)
        {
            if (artifact.IsDirectory)
            {
                Assert.True(
                    Directory.Exists(artifact.Path),
                    $"Startup removed the retained {artifact.Kind} directory.");
            }
            else
            {
                Assert.True(
                    File.Exists(artifact.Path),
                    $"Startup removed the retained {artifact.Kind} file.");
            }
        }
    }

    [Fact]
    public async Task RestartAfterFailedRollback_HonorsDraftBoundaryAndRejectsOldSessionSave()
    {
        await using var fixture = await C6RecoveryFixture.CreateBlockedAsync();
        await using var host = new DesktopApplicationHost(fixture.Options);
        var started = await host.StartAsync();
        var paths = DesktopPaths.Create(fixture.DataRoot);

        var boundaryStore = new DraftRestoreBoundaryStore(paths.DraftRestoreBoundaryPath);
        var boundary = await boundaryStore.ReadAsync();
        Assert.NotNull(boundary);
        Assert.Equal(fixture.DraftIds, boundary!.DraftIds);
        Assert.Equal(fixture.DraftBoundarySha256, await C6RecoveryFixture.Sha256Async(
            paths.DraftRestoreBoundaryPath));

        var drafts = new FileNoteDraftStore(paths.DraftDirectory);
        var storedDrafts = await drafts.ListAsync(noteId: null);
        Assert.Single(storedDrafts);
        Assert.Equal(fixture.DraftIds[0], storedDrafts[0].DraftId);

        var notes = new SqliteNoteRepository(new SqliteConnectionFactory(paths.DatabasePath));
        var saves = new NoteSaveCoordinator(
            notes,
            drafts,
            writeGate: host.WriteGate);
        var workspace = new EditorWorkspaceService(notes, saves, drafts);
        var recovery = new DraftRecoveryService(
            paths,
            notes,
            drafts,
            workspace,
            boundaryStore);
        var inventory = await recovery.ListAsync();
        var item = Assert.Single(inventory.Items);
        Assert.True(item.IsPreRestore);
        Assert.Equal(fixture.DraftIds[0], item.Draft.DraftId);

        var staleNote = fixture.StaleNote with
        {
            Body = "C6-SYNTHETIC stale session body that must never be committed",
            ModifiedAtUtc = DateTimeOffset.UtcNow
        };
        var totalBeforeSave = await C6RecoveryFixture.CountNotesAsync(notes);
        var save = await saves.UpdateAsync(
            staleNote,
            fixture.StaleNoteVersion,
            fixture.StaleNoteTagIds,
            fixture.DraftIds[0]);

        Assert.Equal(SaveStatus.StorageUnavailable, save.Status);
        Assert.Equal(SaveErrorCode.StorageUnavailable, save.Error!.Code);
        Assert.Equal("database_writes_blocked", save.Error.Diagnostics);
        Assert.Equal(DraftDisposition.Retained, save.DraftDisposition);
        Assert.NotNull(await drafts.GetAsync(fixture.DraftIds[0]));
        Assert.Equal(
            fixture.DraftBoundarySha256,
            await C6RecoveryFixture.Sha256Async(paths.DraftRestoreBoundaryPath));
        Assert.Equal(totalBeforeSave, await C6RecoveryFixture.CountNotesAsync(notes));
        // The pre-restore note was switched out; only the protection package
        // and rollback database still contain it.
        Assert.Null(await notes.GetAsync(fixture.StaleNote.Id));

        var verification = await C6RecoveryVerifier.VerifyAsync(
            fixture.DataRoot,
            started.Assessment!,
            fixture.CreateBlockedExpectation());
        Assert.True(verification.IsValid, verification.Describe());
    }

    [Fact]
    public async Task RecoveryVerifier_RejectsReadyAssessmentWhenDiskStateIsWriteBlocked()
    {
        using var temp = new C6TemporaryDirectory();
        var dataRoot = Path.Combine(temp.RootPath, "data");
        Directory.CreateDirectory(dataRoot);
        var paths = DesktopPaths.Create(dataRoot);
        await new SqliteSchema(new SqliteConnectionFactory(paths.DatabasePath)).InitializeAsync();
        var stateStore = new ApplicationStateStore(paths.StatePath);
        var blocked = ApplicationState
            .CreateInitializing(Guid.NewGuid(), DateTimeOffset.UtcNow)
            .MarkReady(2, DateTimeOffset.UtcNow)
            .MarkWritesBlocked("restore_rollback_failed", DateTimeOffset.UtcNow);
        await stateStore.WriteAsync(blocked);
        var assessment = new StartupAssessment
        {
            Mode = StartupMode.Ready,
            Reason = StartupReason.None,
            Message = "Fabricated ready assessment.",
            State = blocked,
            DatabaseInfo = new RestoreDatabaseInfo(2, new RestoreCounts(1, 0, 0, 0, 0, 0))
        };

        var verification = await C6RecoveryVerifier.VerifyAsync(
            dataRoot,
            assessment,
            new C6RecoveryExpectation { ExpectWritesBlocked = true });

        Assert.False(verification.IsValid);
        Assert.Contains(
            verification.Violations,
            violation => violation.Contains("WriteBlocked", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecoveryVerifier_RejectsReadOnlyDiagnosticWithoutAnExplicitReason()
    {
        using var temp = new C6TemporaryDirectory();
        var dataRoot = Path.Combine(temp.RootPath, "data");
        Directory.CreateDirectory(dataRoot);
        var paths = DesktopPaths.Create(dataRoot);
        await new SqliteSchema(new SqliteConnectionFactory(paths.DatabasePath)).InitializeAsync();
        await new ApplicationStateStore(paths.StatePath).WriteAsync(
            ApplicationState
                .CreateInitializing(Guid.NewGuid(), DateTimeOffset.UtcNow)
                .MarkReady(2, DateTimeOffset.UtcNow));
        var assessment = new StartupAssessment
        {
            Mode = StartupMode.ReadOnlyDiagnostic,
            Reason = StartupReason.None,
            Message = "Fabricated unnamed diagnostic."
        };

        var verification = await C6RecoveryVerifier.VerifyAsync(
            dataRoot,
            assessment,
            new C6RecoveryExpectation { RequireDatabaseUsable = false });

        Assert.False(verification.IsValid);
        Assert.Contains(
            verification.Violations,
            violation => violation.Contains("ReadOnlyDiagnostic", StringComparison.Ordinal));
    }

    [C6OptInProcessFact]
    public async Task FailedRollback_ProcessRestart_ReportsWriteBlockedWithRetainedEvidence()
    {
        using var temp = new C6TemporaryDirectory();
        var runId = Path.GetFileName(temp.RootPath);
        var dataRoot = Path.Combine(temp.RootPath, "data");
        var sourceRoot = Path.Combine(temp.RootPath, "source");
        var backupDirectory = Path.Combine(temp.RootPath, "backups");
        var checkpointDirectory = Path.Combine(temp.RootPath, "checkpoints");
        var setupCheckpoints = Path.Combine(temp.RootPath, "setup-checkpoints");
        await SeedScenario.RunAsync(HarnessOptions.Parse(new[]
        {
            "seed", "--data-root", dataRoot, "--notes", "12", "--visible", "2", "--run-id", runId
        })).WaitAsync(TestTimeout);
        await SeedScenario.RunAsync(HarnessOptions.Parse(new[]
        {
            "seed", "--data-root", sourceRoot, "--notes", "5", "--visible", "1", "--run-id", runId
        })).WaitAsync(TestTimeout);
        var backup = await BackupScenario.RunAsync(HarnessOptions.Parse(new[]
        {
            "backup",
            "--data-root", sourceRoot,
            "--backup-dir", backupDirectory,
            "--checkpoint-dir", setupCheckpoints,
            "--run-id", runId,
            "--notes", "5",
            "--visible", "1"
        })).WaitAsync(TestTimeout);
        Assert.True(backup.Passed);
        var packagePath = Path.Combine(backupDirectory, backup.Backup!.NewPackageFileName!);

        var restore = await C6ExternalHarness.RunAsync(
            new[]
            {
                "restore",
                "--data-root", dataRoot,
                "--backup-dir", backupDirectory,
                "--package", packagePath,
                "--checkpoint-dir", checkpointDirectory,
                "--run-id", runId,
                "--notes", "12",
                "--visible", "2",
                "--fault-rollback"
            },
            TestTimeout);
        Assert.Equal(0, restore.ExitCode);
        Assert.Equal("writes_blocked", restore.Result.Status);
        Assert.True(restore.Result.Passed);

        var restart = await C6ExternalHarness.RunAsync(
            new[]
            {
                "restart",
                "--data-root", dataRoot,
                "--backup-dir", backupDirectory,
                "--run-id", runId
            },
            TestTimeout);
        Assert.Equal(0, restart.ExitCode);
        Assert.Equal("ok", restart.Result.Status);
        var probe = restart.Result.RestartProbe!;
        Assert.Equal("WriteBlocked", probe.StartupMode);
        Assert.True(probe.WritesBlocked);
        Assert.True(probe.DatabaseReadable);
        Assert.Equal("Failed", probe.RestoreJournalStage);
        Assert.True(probe.TotalNoteCount > 0);
        Assert.Contains(
            probe.RecoveryArtifacts,
            artifact => artifact.Kind == "RestoreDirectory");
        Assert.All(probe.BackupPackages, package => Assert.True(package.Valid));
    }
}

/// <summary>
/// Creates the cross-restart fixture for a failed restore rollback. The restore
/// itself is the real <see cref="RestoreScenario"/> with the harness-only
/// rollback fault; the coordinator, snapshot, package, switch, journal, state
/// store, and draft store are all production components.
/// </summary>
internal sealed class C6RecoveryFixture : IAsyncDisposable
{
    private readonly C6TemporaryDirectory _temp;

    private C6RecoveryFixture(
        C6TemporaryDirectory temp,
        string runId,
        string dataRoot,
        string backupDirectory,
        int originalNoteCount,
        int packageNoteCount,
        string protectionSnapshotPath,
        string rollbackDatabasePath,
        IReadOnlyList<C6RetainedArtifact> retainedArtifacts,
        IReadOnlyList<Guid> draftIds,
        string draftBoundarySha256,
        QuickNotes.Domain.Note staleNote,
        IReadOnlyList<Guid> staleNoteTagIds,
        long staleNoteVersion)
    {
        _temp = temp;
        RunId = runId;
        DataRoot = dataRoot;
        BackupDirectory = backupDirectory;
        OriginalNoteCount = originalNoteCount;
        PackageNoteCount = packageNoteCount;
        ProtectionSnapshotPath = protectionSnapshotPath;
        RollbackDatabasePath = rollbackDatabasePath;
        RetainedArtifacts = retainedArtifacts;
        DraftIds = draftIds;
        DraftBoundarySha256 = draftBoundarySha256;
        StaleNote = staleNote;
        StaleNoteTagIds = staleNoteTagIds;
        StaleNoteVersion = staleNoteVersion;
    }

    public string RunId { get; }
    public string DataRoot { get; }
    public string BackupDirectory { get; }
    public int OriginalNoteCount { get; }
    public int PackageNoteCount { get; }
    public string ProtectionSnapshotPath { get; }
    public string RollbackDatabasePath { get; }
    public IReadOnlyList<C6RetainedArtifact> RetainedArtifacts { get; }
    public IReadOnlyList<Guid> DraftIds { get; }
    public string DraftBoundarySha256 { get; }
    public QuickNotes.Domain.Note StaleNote { get; }
    public IReadOnlyList<Guid> StaleNoteTagIds { get; }
    public long StaleNoteVersion { get; }

    public string DatabasePath => Path.Combine(DataRoot, "notes.db");
    public string JournalPath => Path.Combine(DataRoot, "recovery-journal.json");

    public DesktopStartupOptions Options => new()
    {
        DataRoot = DataRoot,
        BackupDirectory = BackupDirectory
    };

    public static async Task<C6RecoveryFixture> CreateBlockedAsync(
        CancellationToken cancellationToken = default)
    {
        var timeout = TimeSpan.FromMinutes(3);
        var temp = new C6TemporaryDirectory();
        try
        {
            var runId = Path.GetFileName(temp.RootPath);
            var dataRoot = Path.Combine(temp.RootPath, "data");
            var sourceRoot = Path.Combine(temp.RootPath, "source");
            var backupDirectory = Path.Combine(temp.RootPath, "backups");
            var checkpointDirectory = Path.Combine(temp.RootPath, "checkpoints");
            var setupCheckpoints = Path.Combine(temp.RootPath, "setup-checkpoints");

            var seeded = await SeedScenario.RunAsync(HarnessOptions.Parse(new[]
            {
                "seed",
                "--data-root", dataRoot,
                "--notes", "12",
                "--visible", "2",
                "--run-id", runId
            }), cancellationToken).WaitAsync(timeout, cancellationToken);
            if (!seeded.Passed)
            {
                throw new InvalidOperationException("The C6 recovery fixture could not seed the current root.");
            }

            var sourceSeed = await SeedScenario.RunAsync(HarnessOptions.Parse(new[]
            {
                "seed",
                "--data-root", sourceRoot,
                "--notes", "5",
                "--visible", "1",
                "--run-id", runId
            }), cancellationToken).WaitAsync(timeout, cancellationToken);
            if (!sourceSeed.Passed)
            {
                throw new InvalidOperationException("The C6 recovery fixture could not seed the source root.");
            }

            var backup = await BackupScenario.RunAsync(HarnessOptions.Parse(new[]
            {
                "backup",
                "--data-root", sourceRoot,
                "--backup-dir", backupDirectory,
                "--checkpoint-dir", setupCheckpoints,
                "--run-id", runId,
                "--notes", "5",
                "--visible", "1"
            }), cancellationToken).WaitAsync(timeout, cancellationToken);
            if (!backup.Passed || backup.Backup?.NewPackageFileName is null)
            {
                throw new InvalidOperationException("The C6 recovery fixture could not create the restore package.");
            }

            var packagePath = Path.Combine(backupDirectory, backup.Backup.NewPackageFileName);
            var repository = new SqliteNoteRepository(
                new SqliteConnectionFactory(Path.Combine(dataRoot, "notes.db")));
            var note = (await repository.ListAsync(
                    QuickNotes.Domain.NoteLifecycle.Active,
                    cancellationToken))
                .First();
            var tagIds = await repository.GetTagIdsAsync(note.Id, cancellationToken);
            var draftStore = new FileNoteDraftStore(Path.Combine(dataRoot, "drafts"));
            var draft = new NoteDraft
            {
                DraftId = Guid.NewGuid(),
                NoteId = note.Id,
                BaseVersion = note.Version,
                Title = note.Title,
                Body = $"{C6DataFactory.SyntheticBodyMarker} run={runId} pre-restore draft",
                CategoryId = note.CategoryId,
                TagIds = tagIds,
                CapturedAtUtc = DateTimeOffset.UtcNow
            };
            await draftStore.SaveAsync(draft, cancellationToken);
            var boundary = new DraftRestoreBoundary
            {
                FormatVersion = DraftRestoreBoundary.CurrentFormatVersion,
                RestoreId = Guid.NewGuid(),
                RestoredAtUtc = DateTimeOffset.UtcNow,
                DraftIds = new[] { draft.DraftId }
            };
            var boundaryPath = Path.Combine(dataRoot, "draft-restore-boundary.json");
            await new DraftRestoreBoundaryStore(boundaryPath).WriteAsync(boundary, cancellationToken);
            var boundaryHash = await Sha256Async(boundaryPath, cancellationToken);
            var draftFile = Directory
                .GetFiles(Path.Combine(dataRoot, "drafts"), "*.json")
                .Single();

            var restore = await RestoreScenario.RunAsync(HarnessOptions.Parse(new[]
            {
                "restore",
                "--data-root", dataRoot,
                "--backup-dir", backupDirectory,
                "--package", packagePath,
                "--checkpoint-dir", checkpointDirectory,
                "--run-id", runId,
                "--notes", "12",
                "--visible", "2",
                "--fault-rollback"
            }), cancellationToken).WaitAsync(timeout, cancellationToken);
            if (!restore.Passed || restore.Status != "writes_blocked")
            {
                throw new InvalidOperationException(
                    $"The C6 recovery fixture expected a blocked restore, observed '{restore.Status}' " +
                    $"({restore.ErrorCode}; {restore.Message}).");
            }

            var journalRead = await new FileRestoreJournalStore(
                    Path.Combine(dataRoot, "recovery-journal.json"))
                .ReadAsync(cancellationToken);
            var journal = journalRead.Journal
                ?? throw new InvalidOperationException("The blocked restore did not write a journal.");
            var protectionPath = journal.ProtectionSnapshotPath
                ?? throw new InvalidOperationException("The blocked restore retained no protection snapshot.");
            var rollbackPath = journal.RollbackPath
                ?? throw new InvalidOperationException("The blocked restore retained no rollback database.");
            if (!File.Exists(rollbackPath))
            {
                throw new InvalidOperationException("The retained rollback database is missing.");
            }

            var stagingDirectory = Path.GetDirectoryName(rollbackPath)!;
            var retained = new List<C6RetainedArtifact>
            {
                new("ProtectionSnapshot", protectionPath, false, await Sha256Async(protectionPath, cancellationToken)),
                new("RollbackDatabase", rollbackPath, false, await Sha256Async(rollbackPath, cancellationToken)),
                new("RestoreStaging", stagingDirectory, true, null),
                new("RestoreJournal", Path.Combine(dataRoot, "recovery-journal.json"), false,
                    await Sha256Async(Path.Combine(dataRoot, "recovery-journal.json"), cancellationToken)),
                new("DraftBoundary", boundaryPath, false, boundaryHash),
                new("DraftFile", draftFile, false, await Sha256Async(draftFile, cancellationToken)),
                new("SourcePackage", packagePath, false, await Sha256Async(packagePath, cancellationToken))
            };

            return new C6RecoveryFixture(
                temp,
                runId,
                dataRoot,
                backupDirectory,
                originalNoteCount: 12,
                packageNoteCount: 5,
                protectionPath,
                rollbackPath,
                retained,
                new[] { draft.DraftId },
                boundaryHash,
                note,
                tagIds,
                note.Version);
        }
        catch
        {
            temp.Dispose();
            throw;
        }
    }

    public C6RecoveryExpectation CreateBlockedExpectation() => new()
    {
        ExpectWritesBlocked = true,
        ExpectedJournalStage = RestoreStage.Failed,
        RequireDatabaseUsable = true,
        RequireRollbackPath = true,
        RetainedArtifacts = RetainedArtifacts,
        ExpectedDraftCount = DraftIds.Count,
        ExpectedBoundaryDraftIds = DraftIds
    };

    public ValueTask DisposeAsync()
    {
        _temp.Dispose();
        return ValueTask.CompletedTask;
    }

    public static async Task<string> Sha256Async(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 81920,
            FileOptions.Asynchronous);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static async Task<int> CountNotesAsync(
        INoteRepository repository,
        CancellationToken cancellationToken = default)
    {
        var active = await repository.ListAsync(
            QuickNotes.Domain.NoteLifecycle.Active,
            cancellationToken);
        var archived = await repository.ListAsync(
            QuickNotes.Domain.NoteLifecycle.Archived,
            cancellationToken);
        var trash = await repository.ListAsync(
            QuickNotes.Domain.NoteLifecycle.Trash,
            cancellationToken);
        return active.Count + archived.Count + trash.Count;
    }
}

internal sealed record C6RetainedArtifact(
    string Kind,
    string Path,
    bool IsDirectory,
    string? Sha256);

internal sealed record C6RecoveryExpectation
{
    public bool ExpectWritesBlocked { get; init; }
    public RestoreStage? ExpectedJournalStage { get; init; }
    public bool RequireJournalTerminal { get; init; } = true;
    public bool RequireDatabaseUsable { get; init; } = true;
    public bool RequireRollbackPath { get; init; }
    public IReadOnlyList<C6RetainedArtifact> RetainedArtifacts { get; init; } =
        Array.Empty<C6RetainedArtifact>();
    public int? ExpectedDraftCount { get; init; }
    public IReadOnlyList<Guid> ExpectedBoundaryDraftIds { get; init; } = Array.Empty<Guid>();
}

internal sealed record C6RecoveryVerification(IReadOnlyList<string> Violations)
{
    public bool IsValid => Violations.Count == 0;

    public string Describe() => IsValid
        ? "The cross-restart recovery invariants are satisfied."
        : string.Join(Environment.NewLine, Violations);
}

/// <summary>
/// Reads the startup assessment, the on-disk application state, the restore
/// journal, the current database, retained recovery artifacts, and the draft
/// boundary, then reports every invariant violation instead of stopping at the
/// first one.
/// </summary>
internal static class C6RecoveryVerifier
{
    private static readonly StartupMode[] ExplicitRecoveryModes =
    {
        StartupMode.NeedsRecovery,
        StartupMode.WriteBlocked,
        StartupMode.ReadOnlyDiagnostic
    };

    public static async Task<C6RecoveryVerification> VerifyAsync(
        string dataRoot,
        StartupAssessment assessment,
        C6RecoveryExpectation expectation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        ArgumentNullException.ThrowIfNull(expectation);
        var paths = DesktopPaths.Create(dataRoot);
        var violations = new List<string>();

        var stateRead = await new ApplicationStateStore(paths.StatePath)
            .ReadAsync(cancellationToken);
        var journalRead = await new FileRestoreJournalStore(paths.RestoreJournalPath)
            .ReadAsync(cancellationToken);

        if (stateRead.Status != ApplicationStateReadStatus.Found || stateRead.State is null)
        {
            violations.Add("The on-disk application state is missing or invalid after restart.");
        }
        else
        {
            var diskState = stateRead.State;
            if (assessment.State is null)
            {
                violations.Add("The startup assessment did not report the application state.");
            }
            else if (assessment.State.WritesBlocked != diskState.WritesBlocked)
            {
                violations.Add(
                    "The startup assessment write-block flag differs from the persisted state.");
            }

            if (expectation.ExpectWritesBlocked)
            {
                if (!diskState.WritesBlocked)
                {
                    violations.Add("Persisted writes are not blocked after the failed rollback restart.");
                }

                if (assessment.Mode != StartupMode.WriteBlocked)
                {
                    violations.Add(
                        $"Expected StartupMode.WriteBlocked after a failed rollback, observed {assessment.Mode}.");
                }

                if (assessment.State?.WritesBlocked != true)
                {
                    violations.Add("The startup assessment state does not report writes blocked.");
                }

                if (string.IsNullOrWhiteSpace(diskState.WriteBlockReason))
                {
                    violations.Add("The persisted write block has no reason.");
                }
            }
            else if (!diskState.WritesBlocked
                     && assessment.Mode == StartupMode.WriteBlocked)
            {
                violations.Add("Startup claims writes blocked but the persisted state is not blocked.");
            }
        }

        if (assessment.Mode == StartupMode.FirstRunInitialized)
        {
            violations.Add("First-run initialization is not valid evidence for a recovery restart.");
        }

        if (assessment.Mode == StartupMode.ReadOnlyDiagnostic
            && (assessment.Reason == StartupReason.None
                || string.IsNullOrWhiteSpace(assessment.ErrorCode)))
        {
            violations.Add(
                "ReadOnlyDiagnostic must name an explicit reason and error code to be diagnosable.");
        }

        if (!ExplicitRecoveryModes.Contains(assessment.Mode)
            && assessment.Mode != StartupMode.Ready)
        {
            violations.Add($"Unexpected startup mode {assessment.Mode}.");
        }

        var databaseUsable = await IsDatabaseUsableAsync(
            paths.DatabasePath,
            cancellationToken);
        if (assessment.Mode == StartupMode.Ready && !databaseUsable)
        {
            violations.Add("Startup reported Ready but the current database is not usable.");
        }

        if (expectation.RequireDatabaseUsable && !databaseUsable)
        {
            violations.Add("The current database is missing, unreadable, or empty after restart.");
        }

        if (expectation.ExpectedJournalStage is { } expectedStage)
        {
            if (journalRead.Status != RestoreJournalReadStatus.Found
                || journalRead.Journal is null)
            {
                violations.Add($"The restore journal is missing; expected stage {expectedStage}.");
            }
            else
            {
                if (journalRead.Journal.Stage != expectedStage)
                {
                    violations.Add(
                        $"The restore journal stage is {journalRead.Journal.Stage}; expected {expectedStage}.");
                }

                if (expectation.RequireJournalTerminal && !journalRead.Journal.IsTerminal)
                {
                    violations.Add("The restore journal is not terminal after restart.");
                }

                if (expectation.RequireRollbackPath
                    && (string.IsNullOrWhiteSpace(journalRead.Journal.RollbackPath)
                        || !File.Exists(journalRead.Journal.RollbackPath)))
                {
                    violations.Add("The journal's rollback database was not retained.");
                }
            }
        }

        foreach (var artifact in expectation.RetainedArtifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (artifact.IsDirectory)
            {
                if (!Directory.Exists(artifact.Path))
                {
                    violations.Add($"The retained {artifact.Kind} directory is missing.");
                }

                continue;
            }

            if (!File.Exists(artifact.Path))
            {
                violations.Add($"The retained {artifact.Kind} file is missing.");
                continue;
            }

            if (artifact.Sha256 is not null)
            {
                var actual = await C6RecoveryFixture.Sha256Async(artifact.Path, cancellationToken);
                if (!string.Equals(actual, artifact.Sha256, StringComparison.Ordinal))
                {
                    violations.Add($"The retained {artifact.Kind} file changed after restart.");
                }
            }
        }

        if (expectation.ExpectedDraftCount is { } expectedDraftCount)
        {
            try
            {
                var drafts = await new FileNoteDraftStore(paths.DraftDirectory)
                    .ListAsync(noteId: null, cancellationToken);
                if (drafts.Count != expectedDraftCount)
                {
                    violations.Add(
                        $"The draft count after restart is {drafts.Count}; expected {expectedDraftCount}.");
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                violations.Add($"The draft directory could not be read ({exception.GetType().Name}).");
            }
        }

        if (expectation.ExpectedBoundaryDraftIds.Count > 0)
        {
            var boundary = await new DraftRestoreBoundaryStore(paths.DraftRestoreBoundaryPath)
                .ReadAsync(cancellationToken);
            if (boundary is null)
            {
                violations.Add("The draft restore boundary is missing or invalid after restart.");
            }
            else
            {
                foreach (var draftId in expectation.ExpectedBoundaryDraftIds)
                {
                    if (!boundary.DraftIds.Contains(draftId))
                    {
                        violations.Add($"The draft boundary no longer marks draft {draftId}.");
                    }
                }
            }
        }

        return new C6RecoveryVerification(violations);
    }

    private static async Task<bool> IsDatabaseUsableAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(databasePath))
        {
            return false;
        }

        try
        {
            var info = await new SqliteRestoreStore()
                .InspectAsync(databasePath, cancellationToken);
            return info.SchemaVersion == 2
                && info.Counts.ActiveNotes
                + info.Counts.ArchivedNotes
                + info.Counts.TrashNotes > 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
