using System.Globalization;

namespace QuickNotes.C6.Harness;

public enum HarnessScenario
{
    Save,
    Backup,
    Restore,
    Migrate,
    Perf
}

/// <summary>
/// Parsed C6 harness command line. This is the CLI contract shared with the
/// PowerShell drivers; scenario execution is added by Tasks 2-5.
/// </summary>
public sealed record HarnessOptions
{
    public required HarnessScenario Scenario { get; init; }
    public required string DataRoot { get; init; }
    public string? BackupDirectory { get; init; }
    public required string CheckpointDirectory { get; init; }
    public int NoteCount { get; init; } = C6Budget.HistoricalNotes;
    public int VisibleCount { get; init; } = C6Budget.VisibleWindows;
    public string? PackagePath { get; init; }
    public int SourceSchema { get; init; } = 1;
    public int Iterations { get; init; } = 30;
    public string? OutputPath { get; init; }

    public static HarnessOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0)
        {
            throw new ArgumentException(
                "A scenario is required. Supported scenarios: save, backup, restore, migrate, perf.");
        }

        if (!TryParseScenario(args[0], out var scenario))
        {
            throw new ArgumentException(
                $"Unknown scenario '{args[0]}'. Supported scenarios: save, backup, restore, migrate, perf.");
        }

        string? dataRoot = null;
        string? backupDirectory = null;
        string? checkpointDirectory = null;
        string? packagePath = null;
        string? outputPath = null;
        var noteCount = C6Budget.HistoricalNotes;
        var visibleCount = C6Budget.VisibleWindows;
        var sourceSchema = 1;
        var iterations = 30;

        for (var index = 1; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--data-root":
                    dataRoot = ReadValue(args, ref index, "--data-root");
                    break;
                case "--backup-dir":
                    backupDirectory = ReadValue(args, ref index, "--backup-dir");
                    break;
                case "--checkpoint-dir":
                    checkpointDirectory = ReadValue(args, ref index, "--checkpoint-dir");
                    break;
                case "--package":
                    packagePath = ReadValue(args, ref index, "--package");
                    break;
                case "--notes":
                    noteCount = ReadInt(args, ref index, "--notes", minimum: 1);
                    break;
                case "--visible":
                    visibleCount = ReadInt(args, ref index, "--visible", minimum: 0);
                    break;
                case "--iterations":
                    iterations = ReadInt(args, ref index, "--iterations", minimum: 1);
                    break;
                case "--source-schema":
                    sourceSchema = ReadInt(args, ref index, "--source-schema", minimum: 1);
                    break;
                case "--output":
                    outputPath = ReadValue(args, ref index, "--output");
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{args[index]}'.");
            }
        }

        var parsed = new HarnessOptions
        {
            Scenario = scenario,
            DataRoot = Required(dataRoot, "--data-root"),
            BackupDirectory = backupDirectory,
            CheckpointDirectory = Required(checkpointDirectory, "--checkpoint-dir"),
            NoteCount = noteCount,
            VisibleCount = visibleCount,
            PackagePath = packagePath,
            SourceSchema = sourceSchema,
            Iterations = iterations,
            OutputPath = outputPath
        };
        parsed.Validate();
        return parsed;
    }

    public static string ToCliName(HarnessScenario scenario) =>
        scenario switch
        {
            HarnessScenario.Save => "save",
            HarnessScenario.Backup => "backup",
            HarnessScenario.Restore => "restore",
            HarnessScenario.Migrate => "migrate",
            HarnessScenario.Perf => "perf",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown scenario.")
        };

    private void Validate()
    {
        if (VisibleCount > NoteCount)
        {
            throw new ArgumentException("--visible must not exceed --notes.");
        }

        if (Scenario is HarnessScenario.Backup or HarnessScenario.Restore
            && string.IsNullOrWhiteSpace(BackupDirectory))
        {
            throw new ArgumentException($"--backup-dir is required for the {ToCliName(Scenario)} scenario.");
        }

        if (Scenario == HarnessScenario.Restore && string.IsNullOrWhiteSpace(PackagePath))
        {
            throw new ArgumentException("--package is required for the restore scenario.");
        }

        if (SourceSchema != 1)
        {
            throw new ArgumentException("Only --source-schema 1 is supported.");
        }
    }

    private static bool TryParseScenario(string value, out HarnessScenario scenario)
    {
        switch (value.ToLowerInvariant())
        {
            case "save":
                scenario = HarnessScenario.Save;
                return true;
            case "backup":
                scenario = HarnessScenario.Backup;
                return true;
            case "restore":
                scenario = HarnessScenario.Restore;
                return true;
            case "migrate":
                scenario = HarnessScenario.Migrate;
                return true;
            case "perf":
                scenario = HarnessScenario.Perf;
                return true;
            default:
                scenario = default;
                return false;
        }
    }

    private static string Required(string? value, string option) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"Option {option} is required.")
            : value;

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            throw new ArgumentException($"Option {option} requires a value.");
        }

        index++;
        return args[index];
    }

    private static int ReadInt(string[] args, ref int index, string option, int minimum)
    {
        var value = ReadValue(args, ref index, option);
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed < minimum)
        {
            throw new ArgumentException(
                $"Option {option} requires an integer greater than or equal to {minimum}.");
        }

        return parsed;
    }
}
