using System.Globalization;

namespace QuickNotes.C6.Harness;

public enum HarnessScenario
{
    Save,
    Backup,
    Restore,
    Migrate,
    Perf,
    Seed,
    Restart
}

/// <summary>
/// Parsed C6 harness command line. The CLI contract is shared with the
/// PowerShell drivers. Task 2 realizes save/backup; Task 3 adds restore,
/// migrate, seed, and restart; Task 4 adds the restore pause/single-instance
/// and failed-rollback composition; perf remains Task 5.
/// <see cref="CheckpointDirectory"/> is null for the documented perf, seed,
/// and restart forms, which do not inject checkpoints.
/// </summary>
public sealed record HarnessOptions
{
    public required HarnessScenario Scenario { get; init; }
    public required string DataRoot { get; init; }
    public string? BackupDirectory { get; init; }
    public string? CheckpointDirectory { get; init; }
    public int NoteCount { get; init; } = C6Budget.HistoricalNotes;
    public int VisibleCount { get; init; } = C6Budget.VisibleWindows;
    public string? PackagePath { get; init; }
    public int SourceSchema { get; init; } = 1;
    public int Iterations { get; init; } = 30;
    public string? OutputPath { get; init; }

    /// <summary>
    /// C6 run identity embedded in synthetic note bodies. It is optional in
    /// the parser so existing unit-level forms stay valid, but every evidence
    /// run must pass the same ID the PowerShell driver uses for cleanup.
    /// </summary>
    public string? RunId { get; init; }

    /// <summary>
    /// Draft directory for the save scenario. It defaults to
    /// <c>&lt;data-root&gt;\drafts</c>. The fault driver can place drafts on a
    /// different volume so a full data volume still leaves a recoverable
    /// draft behind.
    /// </summary>
    public string? DraftDirectory { get; init; }

    public bool FaultDiskFull { get; init; }
    public bool FaultOffline { get; init; }

    /// <summary>
    /// Optional checkpoint at which the opt-in process-kill driver should stop
    /// the harness. The sink writes the marker and then holds until the driver
    /// kills this process tree, which makes each kill point deterministic.
    /// </summary>
    public string? KillMarker { get; init; }

    /// <summary>
    /// Restore-only harness fault that makes post-switch verification report a
    /// count mismatch, driving the real coordinator into its rollback path for
    /// C6-K07.
    /// </summary>
    public bool FaultPostSwitch { get; init; }

    /// <summary>
    /// Restart-probe-only note ID. When supplied, the probe reports that
    /// note's stored version so a kill-at-save case can prove the note was not
    /// committed without emitting the body.
    /// </summary>
    public Guid? ProbeNoteId { get; init; }

    /// <summary>
    /// Restore-only pause checkpoint used by the Task 4 second-instance case.
    /// The scenario writes the marker, keeps the harness-written journal (in
    /// production format) at <c>Switching</c>, then waits for the release file
    /// (or the pause timeout) before continuing, so another process can start
    /// while the restore is provably between its journal write and the database
    /// switch. A timeout is reported as a distinct failing <c>pause_timeout</c>
    /// result.
    /// </summary>
    public string? PauseAt { get; init; }

    public int PauseSeconds { get; init; } = 60;

    /// <summary>
    /// Release file for <see cref="PauseAt"/>. When absent the scenario watches
    /// <c>&lt;checkpoint-dir&gt;\&lt;pause marker&gt;.release</c>.
    /// </summary>
    public string? PauseReleasePath { get; init; }

    /// <summary>
    /// Restore-only: acquire the production single-instance lock for the
    /// disposable data root and hold it for the whole restore. The second
    /// instance probe then has to activate this owner or fail explicitly.
    /// </summary>
    public bool SingleInstanceHold { get; init; }

    /// <summary>
    /// Restore-only harness fault: after a successful switch, drive the real
    /// coordinator into its rollback path and make the rollback itself fail.
    /// That is the production <c>WritesBlocked</c> path (C6-M03); it is test
    /// composition only and never a production hook.
    /// </summary>
    public bool FaultRollback { get; init; }

    public HarnessFaultMode FaultMode =>
        FaultDiskFull ? HarnessFaultMode.DiskFull
        : FaultOffline ? HarnessFaultMode.Offline
        : HarnessFaultMode.None;

    public static HarnessOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0)
        {
            throw new ArgumentException(
                "A scenario is required. Supported scenarios: save, backup, restore, migrate, perf, seed, restart.");
        }

        if (!TryParseScenario(args[0], out var scenario))
        {
            throw new ArgumentException(
                $"Unknown scenario '{args[0]}'. Supported scenarios: save, backup, restore, migrate, perf, seed, restart.");
        }

        string? dataRoot = null;
        string? backupDirectory = null;
        string? checkpointDirectory = null;
        string? packagePath = null;
        string? outputPath = null;
        string? runId = null;
        string? draftDirectory = null;
        string? killMarker = null;
        string? pauseAt = null;
        string? pauseReleasePath = null;
        Guid? probeNoteId = null;
        var faultDiskFull = false;
        var faultOffline = false;
        var faultPostSwitch = false;
        var faultRollback = false;
        var singleInstanceHold = false;
        var noteCount = C6Budget.HistoricalNotes;
        var visibleCount = C6Budget.VisibleWindows;
        var sourceSchema = 1;
        var iterations = 30;
        var pauseSeconds = 60;
        var pauseSecondsSpecified = false;

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
                case "--run-id":
                    runId = ReadValue(args, ref index, "--run-id");
                    break;
                case "--draft-dir":
                    draftDirectory = ReadValue(args, ref index, "--draft-dir");
                    break;
                case "--kill-marker":
                    killMarker = ReadValue(args, ref index, "--kill-marker");
                    break;
                case "--pause-at":
                    pauseAt = ReadValue(args, ref index, "--pause-at");
                    break;
                case "--pause-seconds":
                    pauseSeconds = ReadInt(args, ref index, "--pause-seconds", minimum: 1, maximum: 600);
                    pauseSecondsSpecified = true;
                    break;
                case "--pause-release":
                    pauseReleasePath = ReadValue(args, ref index, "--pause-release");
                    break;
                case "--probe-note-id":
                    probeNoteId = ReadGuid(args, ref index, "--probe-note-id");
                    break;
                case "--fault-disk-full":
                    faultDiskFull = true;
                    break;
                case "--fault-offline":
                    faultOffline = true;
                    break;
                case "--fault-post-switch":
                    faultPostSwitch = true;
                    break;
                case "--fault-rollback":
                    faultRollback = true;
                    break;
                case "--single-instance":
                    singleInstanceHold = true;
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
            CheckpointDirectory = checkpointDirectory,
            NoteCount = noteCount,
            VisibleCount = visibleCount,
            PackagePath = packagePath,
            SourceSchema = sourceSchema,
            Iterations = iterations,
            OutputPath = outputPath,
            RunId = runId,
            DraftDirectory = draftDirectory,
            FaultDiskFull = faultDiskFull,
            FaultOffline = faultOffline,
            KillMarker = killMarker,
            FaultPostSwitch = faultPostSwitch,
            ProbeNoteId = probeNoteId,
            PauseAt = pauseAt,
            PauseSeconds = pauseSeconds,
            PauseReleasePath = pauseReleasePath,
            SingleInstanceHold = singleInstanceHold,
            FaultRollback = faultRollback
        };
        parsed.Validate(pauseSecondsSpecified);
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
            HarnessScenario.Seed => "seed",
            HarnessScenario.Restart => "restart",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown scenario.")
        };

    private void Validate(bool pauseSecondsSpecified)
    {
        if (VisibleCount > NoteCount)
        {
            throw new ArgumentException("--visible must not exceed --notes.");
        }

        if (Scenario is HarnessScenario.Save or HarnessScenario.Backup
                or HarnessScenario.Restore or HarnessScenario.Migrate
            && string.IsNullOrWhiteSpace(CheckpointDirectory))
        {
            // Fault-injection scenarios write checkpoint markers so the
            // external kill driver can stop the harness at a stage. perf,
            // seed, and restart do not inject faults and have no checkpoint.
            throw new ArgumentException(
                $"--checkpoint-dir is required for the {ToCliName(Scenario)} scenario.");
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

        if (FaultDiskFull && FaultOffline)
        {
            throw new ArgumentException(
                "Only one fault flag may be supplied: --fault-disk-full or --fault-offline.");
        }

        if (FaultDiskFull && Scenario != HarnessScenario.Save)
        {
            throw new ArgumentException(
                "--fault-disk-full is only supported by the save scenario.");
        }

        if (FaultOffline && Scenario != HarnessScenario.Backup)
        {
            throw new ArgumentException(
                "--fault-offline is only supported by the backup scenario.");
        }

        if (FaultPostSwitch && Scenario != HarnessScenario.Restore)
        {
            throw new ArgumentException(
                "--fault-post-switch is only supported by the restore scenario.");
        }

        if (FaultRollback && Scenario != HarnessScenario.Restore)
        {
            throw new ArgumentException(
                "--fault-rollback is only supported by the restore scenario.");
        }

        if (SingleInstanceHold && Scenario != HarnessScenario.Restore)
        {
            throw new ArgumentException(
                "--single-instance is only supported by the restore scenario.");
        }

        if (PauseAt is not null)
        {
            if (Scenario != HarnessScenario.Restore)
            {
                throw new ArgumentException(
                    "--pause-at is only supported by the restore scenario.");
            }

            if (!string.Equals(PauseAt, CheckpointNames.RestoreBeforeSwitch, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"--pause-at must be '{CheckpointNames.RestoreBeforeSwitch}'.");
            }

            if (string.IsNullOrWhiteSpace(RunId))
            {
                throw new ArgumentException(
                    "--pause-at requires --run-id so the paused restore is scoped to one C6 run.");
            }

            if (KillMarker is not null)
            {
                throw new ArgumentException(
                    "--pause-at cannot be combined with --kill-marker.");
            }
        }

        if (PauseSeconds is < 1 or > 600)
        {
            throw new ArgumentException("--pause-seconds must be between 1 and 600.");
        }

        if (pauseSecondsSpecified && Scenario != HarnessScenario.Restore)
        {
            throw new ArgumentException(
                "--pause-seconds is only supported by the restore scenario.");
        }

        if (PauseReleasePath is not null && PauseAt is null)
        {
            throw new ArgumentException("--pause-release is only valid with --pause-at.");
        }

        if (DraftDirectory is not null && Scenario != HarnessScenario.Save)
        {
            throw new ArgumentException("--draft-dir is only supported by the save scenario.");
        }

        if (RunId is not null && RunId.Trim().Length == 0)
        {
            throw new ArgumentException("Option --run-id requires a non-empty value.");
        }

        if (KillMarker is not null)
        {
            if (!CheckpointNames.IsKnownFor(Scenario, KillMarker))
            {
                throw new ArgumentException(
                    $"--kill-marker '{KillMarker}' is not a checkpoint of the {ToCliName(Scenario)} scenario.");
            }

            if (string.IsNullOrWhiteSpace(RunId))
            {
                throw new ArgumentException(
                    "--kill-marker requires --run-id so a process kill can be scoped to one C6 run.");
            }
        }

        if (ProbeNoteId is not null && Scenario != HarnessScenario.Restart)
        {
            throw new ArgumentException(
                "--probe-note-id is only supported by the restart scenario.");
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
            case "seed":
                scenario = HarnessScenario.Seed;
                return true;
            case "restart":
                scenario = HarnessScenario.Restart;
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

    private static int ReadInt(
        string[] args,
        ref int index,
        string option,
        int minimum,
        int maximum = int.MaxValue)
    {
        var value = ReadValue(args, ref index, option);
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed < minimum
            || parsed > maximum)
        {
            var range = maximum == int.MaxValue
                ? $"greater than or equal to {minimum}"
                : $"between {minimum} and {maximum}";
            throw new ArgumentException(
                $"Option {option} requires an integer {range}.");
        }

        return parsed;
    }

    private static Guid ReadGuid(string[] args, ref int index, string option)
    {
        var value = ReadValue(args, ref index, option);
        if (!Guid.TryParse(value, out var parsed))
        {
            throw new ArgumentException($"Option {option} requires a GUID.");
        }

        return parsed;
    }
}
