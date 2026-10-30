using System.Text.Json;
using QuickNotes.Application;
using QuickNotes.C6.Harness;
using QuickNotes.Infrastructure.Sqlite;

namespace QuickNotes.IntegrationTests.C6;

/// <summary>
/// Safe default-suite coverage for the C6 process-kill plumbing. The real
/// kill matrix is not part of dotnet test: it is run only through
/// <c>scripts/c6/Invoke-C6ProcessKill.ps1</c> with a unique C6 RunId and a
/// retained evidence root. The one process this file terminates is a
/// disposable marker-writing child started by the test itself.
/// </summary>
public sealed class ProcessKillHarnessTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task KillAtMarker_TerminatesOnlyHarnessProcessTree()
    {
        using var temp = new C6TemporaryDirectory();
        var markerDirectory = Path.Combine(temp.RootPath, "markers");
        var child = ProcessHarness.StartFakeCheckpointChild(markerDirectory);
        try
        {
            var marker = Path.Combine(markerDirectory, "test.reached");
            await ProcessHarness.WaitForMarkerAsync(marker, ProcessHarness.MarkerTimeout);

            Assert.True(File.Exists(marker));
            Assert.False(child.HasExited);
            var pidFile = Path.Combine(markerDirectory, "test.pid");
            await ProcessHarness.WaitForMarkerAsync(pidFile, TimeSpan.FromSeconds(5));
            Assert.True(int.TryParse(
                (await File.ReadAllTextAsync(pidFile)).Trim(),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var childPid));
            Assert.True(childPid > 0);

            ProcessHarness.KillProcessTree(child);
            await ProcessHarness.WaitForExitAsync(child, TimeSpan.FromSeconds(20));

            Assert.True(child.HasExited);
        }
        finally
        {
            if (!child.HasExited)
            {
                ProcessHarness.KillProcessTree(child);
            }

            child.Dispose();
        }
    }

    [Theory]
    [InlineData(
        "dotnet \"D:\\repo\\tests\\QuickNotes.C6.Harness\\bin\\Debug\\net10.0\\QuickNotes.C6.Harness.dll\" restore --run-id c6-task3",
        "c6-task3",
        true)]
    [InlineData(
        "\"D:\\repo\\tests\\QuickNotes.C6.Harness\\bin\\Debug\\net10.0\\QuickNotes.C6.Harness.exe\" save --run-id \"c6-task3\"",
        "c6-task3",
        true)]
    [InlineData(
        "dotnet \"D:\\repo\\QuickNotes.C6.Harness.dll\" restore --run-id c6-other",
        "c6-task3",
        false)]
    [InlineData(
        "pwsh -NoProfile -File D:\\repo\\scripts\\c6\\Invoke-C6ProcessKill.ps1 -RunId c6-task3",
        "c6-task3",
        false)]
    [InlineData(
        "powershell.exe -Command \"dotnet D:\\repo\\QuickNotes.C6.Harness.dll restore --run-id c6-task3\"",
        "c6-task3",
        false)]
    [InlineData("dotnet \"D:\\repo\\QuickNotes.C6.Harness.dll\" restore", "c6-task3", false)]
    public void HarnessCommandGuard_AcceptsOnlyDirectExactRunId(
        string commandLine,
        string runId,
        bool expected)
    {
        Assert.Equal(expected, ProcessHarness.IsC6HarnessCommandLine(commandLine, runId));
    }

    [Fact]
    public void CheckpointNames_AreScopedPerScenario()
    {
        Assert.True(CheckpointNames.IsKnownFor(HarnessScenario.Backup, CheckpointNames.BackupBeforePublish));
        Assert.True(CheckpointNames.IsKnownFor(HarnessScenario.Backup, CheckpointNames.BackupAfterPublish));
        Assert.True(CheckpointNames.IsKnownFor(HarnessScenario.Restore, CheckpointNames.RestoreBeforeSwitch));
        Assert.True(CheckpointNames.IsKnownFor(HarnessScenario.Restore, CheckpointNames.RestoreAfterSwitch));
        Assert.True(CheckpointNames.IsKnownFor(HarnessScenario.Restore, CheckpointNames.RestoreRollback));
        Assert.True(CheckpointNames.IsKnownFor(HarnessScenario.Migrate, CheckpointNames.MigrationBefore));
        Assert.True(CheckpointNames.IsKnownFor(HarnessScenario.Migrate, CheckpointNames.MigrationAfter));
        Assert.True(CheckpointNames.IsKnownFor(HarnessScenario.Save, CheckpointNames.SaveAfterDraft));
        Assert.True(CheckpointNames.IsKnownFor(HarnessScenario.Save, CheckpointNames.SaveBeforeCommit));
        Assert.True(CheckpointNames.IsKnownFor(HarnessScenario.Save, CheckpointNames.SaveAfterCommit));
        Assert.False(CheckpointNames.IsKnownFor(HarnessScenario.Backup, CheckpointNames.SaveAfterDraft));
        Assert.False(CheckpointNames.IsKnownFor(HarnessScenario.Restart, CheckpointNames.RestoreBeforeSwitch));
    }

    [Fact]
    public void Parse_SeedAndRestartDoNotRequireCheckpointDirectory()
    {
        var seed = HarnessOptions.Parse(new[]
        {
            "seed", "--data-root", @"C:\QuickNotes-C6\run-1\data", "--notes", "5", "--visible", "1",
            "--run-id", "c6-run-1"
        });
        var restart = HarnessOptions.Parse(new[]
        {
            "restart", "--data-root", @"C:\QuickNotes-C6\run-1\data", "--run-id", "c6-run-1"
        });

        Assert.Equal(HarnessScenario.Seed, seed.Scenario);
        Assert.Equal(HarnessScenario.Restart, restart.Scenario);
        Assert.Null(seed.CheckpointDirectory);
        Assert.Null(restart.CheckpointDirectory);
    }

    [Theory]
    [InlineData("save", "save.after_draft")]
    [InlineData("backup", "backup.before_publish")]
    [InlineData("restore", "restore.before_switch")]
    [InlineData("migrate", "migration.before")]
    public void Parse_AcceptsKnownKillMarkerForFaultScenario(string scenario, string marker)
    {
        var args = new List<string>
        {
            scenario,
            "--data-root", @"C:\QuickNotes-C6\run-1\data",
            "--checkpoint-dir", @"C:\QuickNotes-C6\run-1\checkpoints",
            "--run-id", "c6-run-1",
            "--kill-marker", marker
        };
        if (scenario is "backup" or "restore")
        {
            args.Add("--backup-dir");
            args.Add(@"C:\QuickNotes-C6\run-1\backups");
        }

        if (scenario == "restore")
        {
            args.Add("--package");
            args.Add(@"C:\QuickNotes-C6\run-1\backups\package.zip");
        }

        var options = HarnessOptions.Parse(args.ToArray());

        Assert.Equal(marker, options.KillMarker);
    }

    [Theory]
    [InlineData("save", "backup.before_publish")]
    [InlineData("migrate", "restore.before_switch")]
    [InlineData("restart", "restore.before_switch")]
    public void Parse_RejectsKillMarkerOutsideItsScenario(string scenario, string marker)
    {
        var args = new List<string>
        {
            scenario,
            "--data-root", @"C:\QuickNotes-C6\run-1\data",
            "--checkpoint-dir", @"C:\QuickNotes-C6\run-1\checkpoints",
            "--kill-marker", marker
        };

        var exception = Assert.Throws<ArgumentException>(() => HarnessOptions.Parse(args.ToArray()));

        Assert.Contains("--kill-marker", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsKillMarkerWithoutRunId()
    {
        var exception = Assert.Throws<ArgumentException>(() => HarnessOptions.Parse(new[]
        {
            "save",
            "--data-root", @"C:\QuickNotes-C6\run-1\data",
            "--checkpoint-dir", @"C:\QuickNotes-C6\run-1\checkpoints",
            "--kill-marker", "save.after_draft"
        }));

        Assert.Contains("--run-id", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("save", "--fault-post-switch", null)]
    [InlineData("save", "--probe-note-id", "c6000000-0000-0000-0000-000000000001")]
    public void Parse_RejectsScenarioSpecificOptionsOnWrongScenario(
        string scenario,
        string option,
        string? value)
    {
        var args = new List<string>
        {
            scenario,
            "--data-root", @"C:\QuickNotes-C6\run-1\data",
            "--checkpoint-dir", @"C:\QuickNotes-C6\run-1\checkpoints",
            option
        };
        if (value is not null)
        {
            args.Add(value);
        }

        var exception = Assert.Throws<ArgumentException>(() => HarnessOptions.Parse(args.ToArray()));

        Assert.Contains(option, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_AcceptsRestartProbeNoteId()
    {
        var noteId = Guid.NewGuid();
        var options = HarnessOptions.Parse(new[]
        {
            "restart",
            "--data-root", @"C:\QuickNotes-C6\run-1\data",
            "--run-id", "c6-run-1",
            "--probe-note-id", noteId.ToString("D")
        });

        Assert.Equal(noteId, options.ProbeNoteId);
    }

    [Fact]
    public async Task CheckpointSink_WritesMarkerBeforeHoldingForTheKiller()
    {
        using var temp = new C6TemporaryDirectory();
        var markerDirectory = Path.Combine(temp.RootPath, "markers");
        var sink = new CheckpointFileSink(
            markerDirectory,
            CheckpointNames.SaveAfterDraft,
            TimeSpan.FromMilliseconds(750));
        var hold = Task.Run(() => Assert.Throws<TimeoutException>(
            () => sink.Reached(CheckpointNames.SaveAfterDraft)));

        await ProcessHarness.WaitForMarkerAsync(
            Path.Combine(markerDirectory, $"{CheckpointNames.SaveAfterDraft}.reached"),
            TimeSpan.FromSeconds(5));
        Assert.False(hold.IsCompleted);

        await hold.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(File.Exists(Path.Combine(
            markerDirectory,
            $"{CheckpointNames.SaveAfterDraft}.reached")));
    }

    [Fact]
    public async Task CheckpointDecorators_WriteMarkersAtDocumentedBoundaries()
    {
        using var temp = new C6TemporaryDirectory();
        var sink = new CheckpointFileSink(Path.Combine(temp.RootPath, "markers"));
        var backup = new CheckpointingBackupPackageStore(new StubBackupPackageStore(), sink);
        var restore = new CheckpointingRestoreStore(new StubRestoreStore(), sink);
        var migrator = new CheckpointingSchemaMigrator(new StubSchemaMigrator(), sink);

        await backup.PublishAsync(
            Path.Combine(temp.RootPath, "snapshot.db"),
            2,
            new BackupRequest
            {
                BackupDirectory = Path.Combine(temp.RootPath, "backups"),
                Type = BackupType.Manual
            },
            DateTimeOffset.UtcNow,
            DateTimeOffset.Now);
        await restore.SwitchAsync("staged.db", "current.db", "rollback.db");
        await restore.RollbackAsync("rollback.db", "current.db", "failed.db");
        await migrator.MigrateToCurrentAsync("database.db", rotateIdentity: true);

        AssertMarker(sink, CheckpointNames.BackupBeforePublish);
        AssertMarker(sink, CheckpointNames.BackupAfterPublish);
        AssertMarker(sink, CheckpointNames.RestoreBeforeSwitch);
        AssertMarker(sink, CheckpointNames.RestoreAfterSwitch);
        AssertMarker(sink, CheckpointNames.RestoreRollback);
        AssertMarker(sink, CheckpointNames.MigrationBefore);
        AssertMarker(sink, CheckpointNames.MigrationAfter);
    }

    [Fact]
    public async Task SaveScenario_WritesSaveCheckpointMarkers()
    {
        using var temp = new C6TemporaryDirectory();
        var runId = "c6-save-markers";
        var dataRoot = Path.Combine(temp.RootPath, "data");
        var checkpointDirectory = Path.Combine(temp.RootPath, "markers");
        await C6DataFactory.SeedAsync(dataRoot, notes: 2, visible: 1, runId);
        var options = HarnessOptions.Parse(new[]
        {
            "save",
            "--data-root", dataRoot,
            "--checkpoint-dir", checkpointDirectory,
            "--run-id", runId,
            "--notes", "2",
            "--visible", "1"
        });

        var result = await SaveScenario.RunAsync(options, CancellationToken.None)
            .WaitAsync(TestTimeout);

        Assert.True(result.Passed);
        var sink = new CheckpointFileSink(checkpointDirectory);
        AssertMarker(sink, CheckpointNames.SaveAfterDraft);
        AssertMarker(sink, CheckpointNames.SaveBeforeCommit);
        AssertMarker(sink, CheckpointNames.SaveAfterCommit);
    }

    [Fact]
    public async Task SeedAndRestart_ReportReadyEvidenceWithoutKill()
    {
        using var temp = new C6TemporaryDirectory();
        var runId = "c6-restart-seed";
        var dataRoot = Path.Combine(temp.RootPath, "data");
        var seedOptions = HarnessOptions.Parse(new[]
        {
            "seed", "--data-root", dataRoot, "--notes", "5", "--visible", "1", "--run-id", runId
        });
        var restartOptions = HarnessOptions.Parse(new[]
        {
            "restart", "--data-root", dataRoot, "--run-id", runId
        });

        var seeded = await SeedScenario.RunAsync(seedOptions, CancellationToken.None)
            .WaitAsync(TestTimeout);
        var restarted = await RestartScenario.RunAsync(restartOptions, CancellationToken.None)
            .WaitAsync(TestTimeout);

        Assert.True(seeded.Passed);
        Assert.Equal("ok", seeded.Status);
        Assert.Equal(5, seeded.TotalNoteCount);
        Assert.True(restarted.Passed);
        Assert.Equal("ok", restarted.Status);
        Assert.NotNull(restarted.RestartProbe);
        var probe = restarted.RestartProbe!;
        Assert.Equal("Ready", probe.StartupMode);
        Assert.False(probe.WritesBlocked);
        Assert.True(probe.DatabaseReadable);
        Assert.True(probe.DatabaseExists);
        Assert.Equal(2, probe.SchemaVersionAfterStartup);
        Assert.Equal(5, probe.TotalNoteCount);
        Assert.True(probe.NoteCount > 0);
        Assert.Equal(0, probe.DraftCount);
        Assert.True(probe.MaxNoteVersion >= 1);
        Assert.NotNull(probe.RecoveryArtifacts);
        Assert.NotNull(probe.BackupPackages);
    }

    [Fact]
    public async Task RestoreScenario_WithoutKillSwitchesPackageAndCompletesJournal()
    {
        using var temp = new C6TemporaryDirectory();
        var runId = "c6-restore-ok";
        var sourceRoot = Path.Combine(temp.RootPath, "source");
        var currentRoot = Path.Combine(temp.RootPath, "data");
        var backupDirectory = Path.Combine(temp.RootPath, "backups");
        var checkpointDirectory = Path.Combine(temp.RootPath, "markers");
        var sourceSeed = HarnessOptions.Parse(new[]
        {
            "seed", "--data-root", sourceRoot, "--notes", "5", "--visible", "1", "--run-id", runId
        });
        var currentSeed = HarnessOptions.Parse(new[]
        {
            "seed", "--data-root", currentRoot, "--notes", "12", "--visible", "1", "--run-id", runId
        });
        await SeedScenario.RunAsync(sourceSeed, CancellationToken.None).WaitAsync(TestTimeout);
        var current = await SeedScenario.RunAsync(currentSeed, CancellationToken.None)
            .WaitAsync(TestTimeout);
        Assert.Equal(12, current.TotalNoteCount);
        var backupOptions = HarnessOptions.Parse(new[]
        {
            "backup",
            "--data-root", sourceRoot,
            "--backup-dir", backupDirectory,
            "--checkpoint-dir", Path.Combine(temp.RootPath, "setup-markers"),
            "--run-id", runId,
            "--notes", "5",
            "--visible", "1"
        });
        var backup = await BackupScenario.RunAsync(backupOptions, CancellationToken.None)
            .WaitAsync(TestTimeout);
        Assert.True(backup.Passed);
        var packagePath = Path.Combine(backupDirectory, backup.Backup!.NewPackageFileName!);
        var restoreOptions = HarnessOptions.Parse(new[]
        {
            "restore",
            "--data-root", currentRoot,
            "--backup-dir", backupDirectory,
            "--package", packagePath,
            "--checkpoint-dir", checkpointDirectory,
            "--run-id", runId,
            "--notes", "12",
            "--visible", "1"
        });

        var restored = await RestoreScenario.RunAsync(restoreOptions, CancellationToken.None)
            .WaitAsync(TestTimeout);

        Assert.True(restored.Passed);
        Assert.Equal("ok", restored.Status);
        var state = await new SqliteRestoreStore().InspectAsync(Path.Combine(currentRoot, "notes.db"));
        Assert.Equal(2, state.SchemaVersion);
        Assert.Equal(5, state.Counts.ActiveNotes + state.Counts.ArchivedNotes + state.Counts.TrashNotes);
        var journal = await new QuickNotes.Desktop.Recovery.FileRestoreJournalStore(
                Path.Combine(currentRoot, "recovery-journal.json"))
            .ReadAsync();
        Assert.Equal(RestoreJournalReadStatus.Found, journal.Status);
        Assert.Equal(RestoreStage.Completed, journal.Journal!.Stage);
    }

    [Fact]
    public async Task MigrationScenario_WithoutKillMigratesVersionOneAndKeepsProtection()
    {
        using var temp = new C6TemporaryDirectory();
        var runId = "c6-migrate-ok";
        var dataRoot = Path.Combine(temp.RootPath, "data");
        var options = HarnessOptions.Parse(new[]
        {
            "migrate",
            "--data-root", dataRoot,
            "--checkpoint-dir", Path.Combine(temp.RootPath, "markers"),
            "--run-id", runId,
            "--notes", "6",
            "--visible", "1"
        });

        var migrated = await MigrationScenario.RunAsync(options, CancellationToken.None)
            .WaitAsync(TestTimeout);

        Assert.True(migrated.Passed);
        Assert.Equal("ok", migrated.Status);
        var state = await new SqliteRestoreStore().InspectAsync(Path.Combine(dataRoot, "notes.db"));
        Assert.Equal(2, state.SchemaVersion);
        Assert.Equal(6, state.Counts.ActiveNotes + state.Counts.ArchivedNotes + state.Counts.TrashNotes);
        var protection = Path.Combine(dataRoot, "migration-protection");
        Assert.Single(Directory.GetFiles(protection, "*.db"));
    }

    [Fact]
    public void ProcessKillScript_ExistsAndDeclaresSafetyGuards()
    {
        var scriptPath = Path.Combine(C6ScriptTestSupport.ScriptsRoot(), "Invoke-C6ProcessKill.ps1");
        Assert.True(File.Exists(scriptPath), $"Missing script {scriptPath}.");
        var script = File.ReadAllText(scriptPath);
        foreach (var token in new[]
                 {
                     "Test-C6HarnessProcessIdentity",
                     "Get-C6ProcessCommandLine",
                     "Stop-C6ProcessTree",
                     "Wait-C6Marker",
                     "--kill-marker",
                     "--run-id",
                     "restart",
                     "startupMode",
                     "writesBlocked",
                     "recoveryArtifacts",
                     "Clear-C6Artifacts.ps1",
                     "task-3-evidence"
                 })
        {
            Assert.Contains(token, script, StringComparison.Ordinal);
        }

        foreach (var caseName in new[]
                 {
                     "C6-K01", "C6-K02", "C6-K03", "C6-K04", "C6-K05",
                     "C6-K06", "C6-K07", "C6-K08", "C6-K09"
                 })
        {
            Assert.Contains(caseName, script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ProcessKillScript_HarnessIdentityGuardAcceptsOnlyHarnessRunId()
    {
        var common = Path.Combine(C6ScriptTestSupport.ScriptsRoot(), "C6ScriptCommon.ps1");
        var good = "dotnet \"D:\\repo\\QuickNotes.C6.Harness.dll\" restore --run-id c6-task3";
        var wrongRun = "dotnet \"D:\\repo\\QuickNotes.C6.Harness.dll\" restore --run-id c6-other";
        var pwsh = "pwsh -NoProfile -File D:\\repo\\scripts\\c6\\Invoke-C6ProcessKill.ps1 -RunId c6-task3";
        var command = string.Join(
            Environment.NewLine,
            "$ErrorActionPreference = 'Stop'",
            $". {C6ScriptTestSupport.PsQuote(common)}",
            $"$good = Test-C6HarnessProcessIdentity -CommandLine {C6ScriptTestSupport.PsQuote(good)} -RunId 'c6-task3'",
            $"$wrongRun = Test-C6HarnessProcessIdentity -CommandLine {C6ScriptTestSupport.PsQuote(wrongRun)} -RunId 'c6-task3'",
            $"$pwsh = Test-C6HarnessProcessIdentity -CommandLine {C6ScriptTestSupport.PsQuote(pwsh)} -RunId 'c6-task3'",
            "[ordered]@{ good = $good; wrongRun = $wrongRun; pwsh = $pwsh } | ConvertTo-Json -Compress");

        var result = C6ScriptTestSupport.RunPowerShellCommand(command);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(C6ScriptTestSupport.ParseLastJsonObject(result.StandardOutput).GetRawText());
        Assert.True(document.RootElement.GetProperty("good").GetBoolean());
        Assert.False(document.RootElement.GetProperty("wrongRun").GetBoolean());
        Assert.False(document.RootElement.GetProperty("pwsh").GetBoolean());
    }

    [Fact]
    public void ProcessKillScript_RestartEvidenceGuardRejectsEmptyDatabase()
    {
        var common = Path.Combine(C6ScriptTestSupport.ScriptsRoot(), "C6ScriptCommon.ps1");
        const string good = """
            {"startupMode":"Ready","writesBlocked":false,"databaseReadable":true,
             "databaseExists":true,"noteCount":22,"totalNoteCount":30,
             "draftCountBeforeStartup":1,"draftCountAfterStartup":1,"draftCount":1,
             "recoveryArtifacts":[],"backupPackages":[]}
            """;
        const string empty = """
            {"startupMode":"NeedsRecovery","writesBlocked":true,"databaseReadable":true,
             "databaseExists":true,"noteCount":0,"totalNoteCount":0,
             "draftCountBeforeStartup":0,"draftCountAfterStartup":0,"draftCount":0,
             "recoveryArtifacts":[],"backupPackages":[]}
            """;
        const string missing = """
            {"startupMode":"Ready","writesBlocked":false,"databaseReadable":true,"noteCount":5}
            """;
        var command = string.Join(
            Environment.NewLine,
            "$ErrorActionPreference = 'Stop'",
            $". {C6ScriptTestSupport.PsQuote(common)}",
            $"$good = Test-C6RestartEvidence -Restart ({C6ScriptTestSupport.PsQuote(good)} | ConvertFrom-Json)",
            $"$empty = Test-C6RestartEvidence -Restart ({C6ScriptTestSupport.PsQuote(empty)} | ConvertFrom-Json)",
            $"$missing = Test-C6RestartEvidence -Restart ({C6ScriptTestSupport.PsQuote(missing)} | ConvertFrom-Json)",
            "[ordered]@{ good = $good.Passed; empty = $empty.Passed; missing = $missing.Passed } | ConvertTo-Json -Compress");

        var result = C6ScriptTestSupport.RunPowerShellCommand(command);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(C6ScriptTestSupport.ParseLastJsonObject(result.StandardOutput).GetRawText());
        Assert.True(document.RootElement.GetProperty("good").GetBoolean());
        Assert.False(document.RootElement.GetProperty("empty").GetBoolean());
        Assert.False(document.RootElement.GetProperty("missing").GetBoolean());
    }

    [Fact(Skip = "Opt-in real process-kill matrix. Run pwsh -File scripts/c6/Invoke-C6ProcessKill.ps1 -RunId <unique C6 id> -Case All -EvidenceRoot <retained dir>. The default suite must never kill a harness process.")]
    public void RealProcessKillMatrix_RunsOnlyThroughExplicitScript()
    {
    }

    private static void AssertMarker(CheckpointFileSink sink, string name)
    {
        _ = sink;
        Assert.True(
            File.Exists(Path.Combine(sink.Directory, $"{name}.reached")),
            $"Missing checkpoint marker '{name}'.");
    }

    private sealed class StubBackupPackageStore : IBackupPackageStore
    {
        public Task<BackupPackageInfo> PublishAsync(
            string snapshotDatabasePath,
            long schemaVersion,
            BackupRequest request,
            DateTimeOffset createdAtUtc,
            DateTimeOffset createdAtLocal,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new BackupPackageInfo(
                Path.Combine(request.BackupDirectory, "quicknotes-test.zip"),
                new BackupManifest
                {
                    FormatVersion = BackupManifest.CurrentFormatVersion,
                    BackupType = request.Type,
                    CreatedAtUtc = createdAtUtc,
                    CreatedAtLocal = createdAtLocal,
                    SchemaVersion = schemaVersion,
                    DatabaseFileName = BackupManifest.DatabaseEntryName,
                    DatabaseSizeBytes = 1,
                    DatabaseSha256 = new string('0', 64)
                }));
    }

    private sealed class StubRestoreStore : IRestoreStore
    {
        public Task<RestoreDatabaseInfo> InspectAsync(
            string databasePath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new RestoreDatabaseInfo(2, new RestoreCounts(1, 0, 0, 1, 0, 0)));

        public Task<string?> TryGetDatabaseHashAsync(
            string databasePath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(new string('a', 64));

        public Task<RestoreDatabaseInfo> PrepareCurrentAsync(
            string currentDatabasePath,
            CancellationToken cancellationToken = default) =>
            InspectAsync(currentDatabasePath, cancellationToken);

        public Task SwitchAsync(
            string stagedDatabasePath,
            string currentDatabasePath,
            string rollbackDatabasePath,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RollbackAsync(
            string rollbackDatabasePath,
            string currentDatabasePath,
            string failedDatabasePath,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class StubSchemaMigrator : ISchemaMigrator
    {
        public Task<SchemaMigrationOutcome> MigrateToCurrentAsync(
            string databasePath,
            bool rotateIdentity,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SchemaMigrationOutcome(1, 2, Guid.NewGuid(), true, rotateIdentity));
    }
}
