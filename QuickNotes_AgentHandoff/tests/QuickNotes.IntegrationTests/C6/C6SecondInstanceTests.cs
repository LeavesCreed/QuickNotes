using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuickNotes.C6.Harness;
using QuickNotes.Desktop.Diagnostics;
using QuickNotes.Desktop.Hosting;
using QuickNotes.Desktop.SingleInstance;
using QuickNotes.Desktop.Startup;

namespace QuickNotes.IntegrationTests.C6;

/// <summary>
/// C6-M01/C6-M02 second-instance evidence. The default cases use the real
/// <see cref="SingleInstanceGuard"/> and <see cref="DesktopApplicationHost"/>
/// against disposable <c>%TEMP%\QuickNotes-C6</c> roots. The cross-process
/// restore-pause case is opt-in (<c>C6_RUN_REAL_PROCESS_CASES=1</c>) because it
/// starts a real harness restore process; the default suite must stay free of
/// external process orchestration.
/// </summary>
public sealed class C6SecondInstanceTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(3);

    [Fact]
    public async Task ConcurrentStart_ExactlyOneOwnerPerDataRoot()
    {
        using var temp = new C6TemporaryDirectory();
        var log = new DiagnosticLog(Path.Combine(temp.RootPath, "logs"));
        await using var first = new SingleInstanceGuard(temp.RootPath, log);
        await using var second = new SingleInstanceGuard(temp.RootPath, log);

        var results = await Task.WhenAll(
                first.StartAsync(TimeSpan.FromSeconds(3)),
                second.StartAsync(TimeSpan.FromSeconds(3)))
            .WaitAsync(TestTimeout);

        Assert.Single(results, result => result == SingleInstanceStartResult.Owner);
        Assert.All(
            results.Where(result => result != SingleInstanceStartResult.Owner),
            result => Assert.True(
                result is SingleInstanceStartResult.ActivatedExisting
                    or SingleInstanceStartResult.Unresponsive,
                $"Unexpected non-owner result {result}."));
    }

    [Fact]
    public async Task SecondInstance_WhileOwnerIsRunning_ActivatesWithoutWriting()
    {
        using var temp = new C6TemporaryDirectory();
        await using var owner = new DesktopApplicationHost(new DesktopStartupOptions
        {
            DataRoot = temp.RootPath
        });
        var ownerStart = await owner.StartAsync();
        Assert.Equal(SingleInstanceStartResult.Owner, ownerStart.InstanceResult);
        Assert.Equal(StartupMode.FirstRunInitialized, ownerStart.Assessment!.Mode);

        var databasePath = owner.Paths.DatabasePath;
        var statePath = owner.Paths.StatePath;
        var databaseHash = await C6RecoveryFixture.Sha256Async(databasePath);
        var stateHash = await C6RecoveryFixture.Sha256Async(statePath);
        var activated = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        owner.ActivationRequested += () => activated.TrySetResult();

        await using var second = new DesktopApplicationHost(new DesktopStartupOptions
        {
            DataRoot = temp.RootPath
        });
        var secondStart = await second.StartAsync();

        Assert.Equal(SingleInstanceStartResult.ActivatedExisting, secondStart.InstanceResult);
        Assert.False(secondStart.CanShowWindow);
        Assert.Equal(0, secondStart.ExitCode);
        Assert.Null(secondStart.Assessment);
        Assert.Null(second.EditingServices);
        Assert.Null(second.BackupServices);
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(databaseHash, await C6RecoveryFixture.Sha256Async(databasePath));
        Assert.Equal(stateHash, await C6RecoveryFixture.Sha256Async(statePath));
    }

    [Fact]
    public async Task SecondInstance_WhenActivationServerIsMissing_ReturnsClearFailure()
    {
        using var temp = new C6TemporaryDirectory();
        Directory.CreateDirectory(temp.RootPath);
        // Mirrors SingleInstanceGuard's private lock file name. Holding it
        // without starting the activation pipe is the "owner is unresponsive"
        // state a second desktop instance must diagnose clearly.
        using var heldLock = new FileStream(
            Path.Combine(temp.RootPath, ".quicknotes-instance.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        await using var host = new DesktopApplicationHost(new DesktopStartupOptions
        {
            DataRoot = temp.RootPath
        });
        var result = await host.StartAsync();

        Assert.Equal(SingleInstanceStartResult.Unresponsive, result.InstanceResult);
        Assert.False(result.CanShowWindow);
        Assert.Equal(2, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
        Assert.False(File.Exists(Path.Combine(temp.RootPath, "notes.db")));
        Assert.False(File.Exists(Path.Combine(temp.RootPath, "application-state.json")));
    }

    [Fact]
    public void Parse_AcceptsRestorePauseAndSingleInstanceOptions()
    {
        var options = HarnessOptions.Parse(new[]
        {
            "restore",
            "--data-root", @"C:\QuickNotes-C6\run-1\data",
            "--backup-dir", @"C:\QuickNotes-C6\run-1\backups",
            "--package", @"C:\QuickNotes-C6\run-1\backups\package.zip",
            "--checkpoint-dir", @"C:\QuickNotes-C6\run-1\checkpoints",
            "--run-id", "c6-run-1",
            "--pause-at", "restore.before_switch",
            "--pause-seconds", "45",
            "--pause-release", @"C:\QuickNotes-C6\run-1\release",
            "--single-instance"
        });

        Assert.Equal("restore.before_switch", options.PauseAt);
        Assert.Equal(45, options.PauseSeconds);
        Assert.Equal(@"C:\QuickNotes-C6\run-1\release", options.PauseReleasePath);
        Assert.True(options.SingleInstanceHold);
        Assert.False(options.FaultRollback);
    }

    [Fact]
    public void Parse_AcceptsFaultRollbackForRestore()
    {
        var options = HarnessOptions.Parse(new[]
        {
            "restore",
            "--data-root", @"C:\QuickNotes-C6\run-1\data",
            "--backup-dir", @"C:\QuickNotes-C6\run-1\backups",
            "--package", @"C:\QuickNotes-C6\run-1\backups\package.zip",
            "--checkpoint-dir", @"C:\QuickNotes-C6\run-1\checkpoints",
            "--run-id", "c6-run-1",
            "--fault-rollback"
        });

        Assert.True(options.FaultRollback);
        Assert.Null(options.PauseAt);
    }

    [Theory]
    [InlineData("save", "--pause-at", "restore.before_switch")]
    [InlineData("save", "--pause-seconds", "30")]
    [InlineData("seed", "--single-instance", null)]
    [InlineData("save", "--fault-rollback", null)]
    public void Parse_RejectsRestoreCompetitionOptionsOnOtherScenarios(
        string scenario,
        string option,
        string? value)
    {
        var args = new List<string>
        {
            scenario,
            "--data-root", @"C:\QuickNotes-C6\run-1\data",
            option
        };
        if (value is not null)
        {
            args.Add(value);
        }

        if (scenario is "save" or "restore")
        {
            args.Add("--checkpoint-dir");
            args.Add(@"C:\QuickNotes-C6\run-1\checkpoints");
        }

        if (scenario is "save" or "backup" or "restore")
        {
            args.Add("--backup-dir");
            args.Add(@"C:\QuickNotes-C6\run-1\backups");
        }

        if (scenario == "restore")
        {
            args.Add("--package");
            args.Add(@"C:\QuickNotes-C6\run-1\backups\package.zip");
        }

        var exception = Assert.Throws<ArgumentException>(() => HarnessOptions.Parse(args.ToArray()));

        Assert.Contains(option, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsPauseWithoutRunId()
    {
        var exception = Assert.Throws<ArgumentException>(() => HarnessOptions.Parse(new[]
        {
            "restore",
            "--data-root", @"C:\QuickNotes-C6\run-1\data",
            "--backup-dir", @"C:\QuickNotes-C6\run-1\backups",
            "--package", @"C:\QuickNotes-C6\run-1\backups\package.zip",
            "--checkpoint-dir", @"C:\QuickNotes-C6\run-1\checkpoints",
            "--pause-at", "restore.before_switch"
        }));

        Assert.Contains("--run-id", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsPauseCombinedWithKillMarker()
    {
        var exception = Assert.Throws<ArgumentException>(() => HarnessOptions.Parse(new[]
        {
            "restore",
            "--data-root", @"C:\QuickNotes-C6\run-1\data",
            "--backup-dir", @"C:\QuickNotes-C6\run-1\backups",
            "--package", @"C:\QuickNotes-C6\run-1\backups\package.zip",
            "--checkpoint-dir", @"C:\QuickNotes-C6\run-1\checkpoints",
            "--run-id", "c6-run-1",
            "--kill-marker", CheckpointNames.RestoreBeforeSwitch,
            "--pause-at", CheckpointNames.RestoreBeforeSwitch
        }));

        Assert.Contains("--kill-marker", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("601")]
    [InlineData("not-a-number")]
    public void Parse_RejectsInvalidPauseSeconds(string seconds)
    {
        var exception = Assert.Throws<ArgumentException>(() => HarnessOptions.Parse(new[]
        {
            "restore",
            "--data-root", @"C:\QuickNotes-C6\run-1\data",
            "--backup-dir", @"C:\QuickNotes-C6\run-1\backups",
            "--package", @"C:\QuickNotes-C6\run-1\backups\package.zip",
            "--checkpoint-dir", @"C:\QuickNotes-C6\run-1\checkpoints",
            "--run-id", "c6-run-1",
            "--pause-at", "restore.before_switch",
            "--pause-seconds", seconds
        }));

        Assert.Contains("--pause-seconds", exception.Message, StringComparison.Ordinal);
    }

    [C6OptInProcessFact]
    public async Task PausedRestore_SecondInstanceActivatesWithoutCompetingWrite()
    {
        using var temp = new C6TemporaryDirectory();
        var runId = Path.GetFileName(temp.RootPath);
        var dataRoot = Path.Combine(temp.RootPath, "data");
        var sourceRoot = Path.Combine(temp.RootPath, "source");
        var backupDirectory = Path.Combine(temp.RootPath, "backups");
        var checkpointDirectory = Path.Combine(temp.RootPath, "checkpoints");
        var setupCheckpoints = Path.Combine(temp.RootPath, "setup-checkpoints");
        var releasePath = Path.Combine(temp.RootPath, "restore.release");
        var markerPath = Path.Combine(
            checkpointDirectory,
            $"{CheckpointNames.RestoreBeforeSwitch}.reached");
        await SeedScenario.RunAsync(HarnessOptions.Parse(new[]
        {
            "seed", "--data-root", dataRoot, "--notes", "12", "--visible", "2", "--run-id", runId
        }), CancellationToken.None).WaitAsync(TestTimeout);
        await SeedScenario.RunAsync(HarnessOptions.Parse(new[]
        {
            "seed", "--data-root", sourceRoot, "--notes", "5", "--visible", "1", "--run-id", runId
        }), CancellationToken.None).WaitAsync(TestTimeout);
        var backup = await BackupScenario.RunAsync(HarnessOptions.Parse(new[]
        {
            "backup",
            "--data-root", sourceRoot,
            "--backup-dir", backupDirectory,
            "--checkpoint-dir", setupCheckpoints,
            "--run-id", runId,
            "--notes", "5",
            "--visible", "1"
        }), CancellationToken.None).WaitAsync(TestTimeout);
        Assert.True(backup.Passed);
        var packagePath = Path.Combine(backupDirectory, backup.Backup!.NewPackageFileName!);

        using var restore = C6ExternalHarness.Start(new[]
        {
            "restore",
            "--data-root", dataRoot,
            "--backup-dir", backupDirectory,
            "--package", packagePath,
            "--checkpoint-dir", checkpointDirectory,
            "--run-id", runId,
            "--notes", "12",
            "--visible", "2",
            "--pause-at", CheckpointNames.RestoreBeforeSwitch,
            "--pause-seconds", "90",
            "--pause-release", releasePath,
            "--single-instance"
        });

        try
        {
            await ProcessHarness.WaitForMarkerAsync(markerPath, TimeSpan.FromSeconds(60));
            Assert.False(restore.HasExited);

            var databasePath = Path.Combine(dataRoot, "notes.db");
            var statePath = Path.Combine(dataRoot, "application-state.json");
            var journalPath = Path.Combine(dataRoot, "recovery-journal.json");
            var databaseHash = await C6RecoveryFixture.Sha256Async(databasePath);
            var stateHash = await C6RecoveryFixture.Sha256Async(statePath);
            var journalHash = await C6RecoveryFixture.Sha256Async(journalPath);

            await using (var probe = new DesktopApplicationHost(new DesktopStartupOptions
            {
                DataRoot = dataRoot,
                BackupDirectory = backupDirectory
            }))
            {
                var probeStart = await probe.StartAsync();

                Assert.Equal(
                    SingleInstanceStartResult.ActivatedExisting,
                    probeStart.InstanceResult);
                Assert.False(probeStart.CanShowWindow);
                Assert.Equal(0, probeStart.ExitCode);
                Assert.Null(probeStart.Assessment);
                Assert.Null(probe.EditingServices);
                Assert.Null(probe.BackupServices);
            }

            Assert.Equal(databaseHash, await C6RecoveryFixture.Sha256Async(databasePath));
            Assert.Equal(stateHash, await C6RecoveryFixture.Sha256Async(statePath));
            Assert.Equal(journalHash, await C6RecoveryFixture.Sha256Async(journalPath));

            await File.WriteAllTextAsync(releasePath, "release");
            await ProcessHarness.WaitForExitAsync(restore, TimeSpan.FromSeconds(90));
            var stdout = await restore.StandardOutput.ReadToEndAsync();
            var stderr = await restore.StandardError.ReadToEndAsync();
            Assert.True(
                restore.ExitCode == 0,
                $"The paused restore exited with {restore.ExitCode}: {stderr}{stdout}");
            var result = C6ExternalHarness.ParseResult(stdout);
            Assert.True(result.Passed, result.Message);
            Assert.Equal("ok", result.Status);
            Assert.NotNull(result.Instance);
            Assert.Equal("Owner", result.Instance!.StartResult);
            Assert.True(result.Instance.ActivationCount >= 1);
            Assert.NotNull(result.Pause);
            Assert.True(result.Pause!.MarkerWritten);
            Assert.True(result.Pause.Released);

            var journal = await new QuickNotes.Desktop.Recovery.FileRestoreJournalStore(journalPath)
                .ReadAsync();
            Assert.Equal(QuickNotes.Application.RestoreStage.Completed, journal.Journal!.Stage);
            var database = await new QuickNotes.Infrastructure.Sqlite.SqliteRestoreStore()
                .InspectAsync(databasePath);
            Assert.Equal(
                5,
                database.Counts.ActiveNotes
                + database.Counts.ArchivedNotes
                + database.Counts.TrashNotes);
        }
        finally
        {
            if (!restore.HasExited)
            {
                try
                {
                    await File.WriteAllTextAsync(releasePath, "release");
                }
                catch (IOException)
                {
                }

                ProcessHarness.KillProcessTree(restore);
                await ProcessHarness.WaitForExitAsync(restore, TimeSpan.FromSeconds(20));
            }
        }
    }
}

/// <summary>
/// Skips a real cross-process C6 case unless
/// <c>C6_RUN_REAL_PROCESS_CASES=1</c>. The default suite must not start a live
/// restore process or a second desktop probe against it.
/// </summary>
internal sealed class C6OptInProcessFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "C6_RUN_REAL_PROCESS_CASES";

    public C6OptInProcessFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(EnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            Skip = "Opt-in real C6 process case. Run with " +
                $"$env:{EnvironmentVariable}='1' and " +
                "dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/" +
                "QuickNotes.IntegrationTests.csproj --filter \"FullyQualifiedName~C6\" --nologo. " +
                "The default suite must not start a live restore process or a second instance probe.";
        }
    }
}

internal sealed record C6ExternalResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    HarnessScenarioResult Result);

/// <summary>
/// Starts and parses the real <c>QuickNotes.C6.Harness</c> process. It only ever
/// touches a harness DLL shipped next to the tests and arguments constructed by
/// the test; the user's desktop process is never discovered or terminated.
/// </summary>
internal static class C6ExternalHarness
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateOptions();

    public static Process Start(IReadOnlyList<string> arguments)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "QuickNotes.C6.Harness.dll");
        Assert.True(File.Exists(dll), $"Missing harness assembly {dll}.");
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(dll)!
        };
        startInfo.ArgumentList.Add(dll);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The C6 harness process could not be started.");
    }

    public static async Task<C6ExternalResult> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout)
    {
        using var process = Start(arguments);
        await ProcessHarness.WaitForExitAsync(process, timeout);
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        return new C6ExternalResult(
            process.ExitCode,
            stdout,
            stderr,
            ParseResult(stdout));
    }

    public static HarnessScenarioResult ParseResult(string stdout)
    {
        var json = LastJsonLine(stdout)
            ?? throw new InvalidOperationException(
                $"The C6 harness emitted no JSON result. Output: {stdout}");
        return JsonSerializer.Deserialize<HarnessScenarioResult>(json, SerializerOptions)
            ?? throw new InvalidOperationException("The C6 harness result could not be parsed.");
    }

    private static string? LastJsonLine(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.TrimEntries);
        for (var index = lines.Length - 1; index >= 0; index--)
        {
            if (lines[index].StartsWith('{'))
            {
                return lines[index];
            }
        }

        return null;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
