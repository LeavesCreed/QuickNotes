using QuickNotes.C6.Harness;

namespace QuickNotes.IntegrationTests.C6;

/// <summary>
/// Locks the documented C6 harness command lines. Program.cs only translates
/// parse failures into the usage exit code, so exercising
/// <see cref="HarnessOptions.Parse"/> and the runner's exit constants covers
/// the CLI contract without launching a process.
/// </summary>
public sealed class HarnessOptionsTests
{
    private const string DataRoot = @"C:\QuickNotes-C6\run-0001";
    private const string CheckpointDirectory = @"C:\QuickNotes-C6\run-0001\checkpoints";
    private const string BackupDirectory = @"C:\QuickNotes-C6\run-0001\backups";
    private const string PackagePath = @"C:\QuickNotes-C6\run-0001\backups\package.zip";
    private const string OutputPath = @"C:\QuickNotes-C6\run-0001\perf.json";

    [Fact]
    public void Parse_AcceptsDocumentedSaveForm()
    {
        var options = HarnessOptions.Parse(new[]
        {
            "save", "--data-root", DataRoot, "--checkpoint-dir", CheckpointDirectory, "--notes", "1000"
        });

        Assert.Equal(HarnessScenario.Save, options.Scenario);
        Assert.Equal(DataRoot, options.DataRoot);
        Assert.Equal(CheckpointDirectory, options.CheckpointDirectory);
        Assert.Equal(1000, options.NoteCount);
        Assert.Equal(C6Budget.VisibleWindows, options.VisibleCount);
        Assert.Null(options.BackupDirectory);
    }

    [Fact]
    public void Parse_AcceptsDocumentedBackupForm()
    {
        var options = HarnessOptions.Parse(new[]
        {
            "backup", "--data-root", DataRoot, "--backup-dir", BackupDirectory,
            "--checkpoint-dir", CheckpointDirectory
        });

        Assert.Equal(HarnessScenario.Backup, options.Scenario);
        Assert.Equal(BackupDirectory, options.BackupDirectory);
        Assert.Equal(CheckpointDirectory, options.CheckpointDirectory);
    }

    [Fact]
    public void Parse_AcceptsDocumentedRestoreForm()
    {
        var options = HarnessOptions.Parse(new[]
        {
            "restore", "--data-root", DataRoot, "--backup-dir", BackupDirectory,
            "--package", PackagePath, "--checkpoint-dir", CheckpointDirectory
        });

        Assert.Equal(HarnessScenario.Restore, options.Scenario);
        Assert.Equal(PackagePath, options.PackagePath);
        Assert.Equal(CheckpointDirectory, options.CheckpointDirectory);
    }

    [Fact]
    public void Parse_AcceptsDocumentedMigrateForm()
    {
        var options = HarnessOptions.Parse(new[]
        {
            "migrate", "--data-root", DataRoot, "--checkpoint-dir", CheckpointDirectory,
            "--source-schema", "1"
        });

        Assert.Equal(HarnessScenario.Migrate, options.Scenario);
        Assert.Equal(1, options.SourceSchema);
        Assert.Equal(CheckpointDirectory, options.CheckpointDirectory);
    }

    [Fact]
    public void Parse_AcceptsDocumentedPerfFormWithoutCheckpointDirectory()
    {
        var options = HarnessOptions.Parse(new[]
        {
            "perf", "--data-root", DataRoot, "--notes", "1000", "--visible", "20",
            "--iterations", "30", "--output", OutputPath
        });

        Assert.Equal(HarnessScenario.Perf, options.Scenario);
        Assert.Equal(1000, options.NoteCount);
        Assert.Equal(20, options.VisibleCount);
        Assert.Equal(30, options.Iterations);
        Assert.Equal(OutputPath, options.OutputPath);
        Assert.Null(options.CheckpointDirectory);
    }

    [Fact]
    public void Parse_PerfWithoutCheckpointDirectoryStillRejectsVisibleAboveNotes()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => HarnessOptions.Parse(new[]
            {
                "perf", "--data-root", DataRoot, "--notes", "10", "--visible", "11"
            }));

        Assert.Contains("--visible", exception.Message, StringComparison.Ordinal);
        Assert.Contains("--notes", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("save", new string[0])]
    [InlineData("backup", new[] { "--backup-dir", BackupDirectory })]
    [InlineData("restore", new[] { "--backup-dir", BackupDirectory, "--package", PackagePath })]
    [InlineData("migrate", new string[0])]
    public void Parse_RequiresCheckpointDirectoryOutsidePerf(string scenario, string[] extraArgs)
    {
        var args = new List<string> { scenario, "--data-root", DataRoot };
        args.AddRange(extraArgs);

        var exception = Assert.Throws<ArgumentException>(() => HarnessOptions.Parse(args.ToArray()));

        Assert.Contains("--checkpoint-dir", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RequiresDataRoot()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => HarnessOptions.Parse(new[] { "perf", "--notes", "10" }));

        Assert.Contains("--data-root", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RequiresBackupDirectoryForBackupAndRestore()
    {
        var backup = Assert.Throws<ArgumentException>(
            () => HarnessOptions.Parse(new[]
            {
                "backup", "--data-root", DataRoot, "--checkpoint-dir", CheckpointDirectory
            }));
        var restore = Assert.Throws<ArgumentException>(
            () => HarnessOptions.Parse(new[]
            {
                "restore", "--data-root", DataRoot, "--checkpoint-dir", CheckpointDirectory,
                "--package", PackagePath
            }));

        Assert.Contains("--backup-dir", backup.Message, StringComparison.Ordinal);
        Assert.Contains("--backup-dir", restore.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RequiresPackageForRestore()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => HarnessOptions.Parse(new[]
            {
                "restore", "--data-root", DataRoot, "--backup-dir", BackupDirectory,
                "--checkpoint-dir", CheckpointDirectory
            }));

        Assert.Contains("--package", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsUnknownOption()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => HarnessOptions.Parse(new[]
            {
                "save", "--data-root", DataRoot, "--checkpoint-dir", CheckpointDirectory,
                "--unknown-option", "value"
            }));

        Assert.Contains("Unknown option", exception.Message, StringComparison.Ordinal);
        Assert.Contains("--unknown-option", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsEmptyArgumentsAndUnknownScenario()
    {
        var empty = Assert.Throws<ArgumentException>(() => HarnessOptions.Parse(Array.Empty<string>()));
        var unknown = Assert.Throws<ArgumentException>(
            () => HarnessOptions.Parse(new[] { "soak" }));

        Assert.Contains("scenario is required", empty.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Unknown scenario", unknown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsOptionWithoutValue()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => HarnessOptions.Parse(new[] { "save", "--data-root" }));

        Assert.Contains("--data-root", exception.Message, StringComparison.Ordinal);
        Assert.Contains("requires a value", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--notes", "0")]
    [InlineData("--notes", "not-a-number")]
    [InlineData("--visible", "-1")]
    [InlineData("--iterations", "0")]
    public void Parse_RejectsInvalidCounts(string option, string value)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => HarnessOptions.Parse(new[]
            {
                "perf", "--data-root", DataRoot, option, value
            }));

        Assert.Contains(option, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("0")]
    public void Parse_RejectsUnsupportedSourceSchema(string sourceSchema)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => HarnessOptions.Parse(new[]
            {
                "migrate", "--data-root", DataRoot, "--checkpoint-dir", CheckpointDirectory,
                "--source-schema", sourceSchema
            }));

        Assert.Contains("--source-schema", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HarnessRunner_KnownScenarioReportsNotImplementedExitCode()
    {
        var options = HarnessOptions.Parse(new[]
        {
            "perf", "--data-root", DataRoot, "--notes", "1000", "--visible", "20",
            "--iterations", "30", "--output", OutputPath
        });

        var exitCode = await HarnessRunner.RunAsync(options, CancellationToken.None);

        Assert.Equal(HarnessRunner.NotImplementedExitCode, exitCode);
        Assert.Equal(78, HarnessRunner.NotImplementedExitCode);
        Assert.Equal(2, HarnessRunner.UsageExitCode);
    }
}
