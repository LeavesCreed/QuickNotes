using System.Diagnostics;
using System.Text;
using QuickNotes.C6.Harness;

namespace QuickNotes.IntegrationTests.C6;

/// <summary>
/// Safe process helpers shared by the C6 process-kill tests. Nothing here is
/// used by production code. <see cref="KillProcessTree"/> is only ever called
/// for a process the test itself started and whose command line was verified
/// as a C6 harness invocation (M1) or as a disposable marker-writing child
/// (the Step 1 plumbing test). No user process is ever enumerated or killed.
/// </summary>
internal static class ProcessHarness
{
    public static readonly TimeSpan MarkerTimeout = TimeSpan.FromSeconds(20);

    public static Process StartFakeCheckpointChild(string markerDirectory)
    {
        if (string.IsNullOrWhiteSpace(markerDirectory))
        {
            throw new ArgumentException("Marker directory must not be empty.", nameof(markerDirectory));
        }

        Directory.CreateDirectory(markerDirectory);
        var markerPath = Path.Combine(markerDirectory, "test.reached");
        var pidPath = Path.Combine(markerDirectory, "test.pid");
        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(
            $"$PID | Set-Content -LiteralPath {PsQuote(pidPath)} -Encoding ascii; " +
            $"Set-Content -LiteralPath {PsQuote(markerPath)} -Value reached -Encoding ascii; " +
            "Start-Sleep -Seconds 120");
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The fake checkpoint child could not be started.");
    }

    public static async Task WaitForMarkerAsync(string markerPath, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(markerPath))
            {
                return;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        throw new TimeoutException($"The marker '{markerPath}' was not written within {timeout}.");
    }

    /// <summary>
    /// Starts the real harness assembly next to the tests with redirected
    /// stdout/stderr. Only arguments derived from the parsed
    /// <see cref="HarnessOptions"/> are passed, so a C6 test can never point
    /// the child at a path it did not construct itself.
    /// </summary>
    public static Task<Process> StartAsync(
        string harnessAssembly,
        HarnessOptions options,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        if (string.IsNullOrWhiteSpace(harnessAssembly))
        {
            throw new ArgumentException("Harness assembly must not be empty.", nameof(harnessAssembly));
        }

        ArgumentNullException.ThrowIfNull(options);
        if (!File.Exists(harnessAssembly))
        {
            throw new FileNotFoundException("The harness assembly was not found.", harnessAssembly);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(harnessAssembly)!
        };
        startInfo.ArgumentList.Add(harnessAssembly);
        foreach (var argument in BuildArguments(options))
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The C6 harness process could not be started.");
        return Task.FromResult(process);
    }

    /// <summary>Kills only a process this test started, including its tree.</summary>
    public static Task KillTreeAsync(Process process)
    {
        KillProcessTree(process);
        return Task.CompletedTask;
    }

    private static IReadOnlyList<string> BuildArguments(HarnessOptions options)
    {
        var arguments = new List<string>
        {
            HarnessOptions.ToCliName(options.Scenario),
            "--data-root", options.DataRoot,
            "--notes", options.NoteCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--visible", options.VisibleCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--iterations", options.Iterations.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--source-schema", options.SourceSchema.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        AddIfSet(arguments, "--backup-dir", options.BackupDirectory);
        AddIfSet(arguments, "--checkpoint-dir", options.CheckpointDirectory);
        AddIfSet(arguments, "--package", options.PackagePath);
        AddIfSet(arguments, "--output", options.OutputPath);
        AddIfSet(arguments, "--run-id", options.RunId);
        AddIfSet(arguments, "--draft-dir", options.DraftDirectory);
        AddIfSet(arguments, "--kill-marker", options.KillMarker);
        AddIfSet(arguments, "--pause-at", options.PauseAt);
        AddIfSet(arguments, "--pause-release", options.PauseReleasePath);
        if (options.ProbeNoteId is { } probeNoteId)
        {
            arguments.Add("--probe-note-id");
            arguments.Add(probeNoteId.ToString("D"));
        }

        if (options.FaultDiskFull)
        {
            arguments.Add("--fault-disk-full");
        }

        if (options.FaultOffline)
        {
            arguments.Add("--fault-offline");
        }

        if (options.FaultPostSwitch)
        {
            arguments.Add("--fault-post-switch");
        }

        if (options.FaultRollback)
        {
            arguments.Add("--fault-rollback");
        }

        if (options.SingleInstanceHold)
        {
            arguments.Add("--single-instance");
        }

        return arguments;
    }

    private static void AddIfSet(
        ICollection<string> arguments,
        string option,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        arguments.Add(option);
        arguments.Add(value);
    }

    /// <summary>
    /// Mirrors the PowerShell guard in C6ScriptCommon.ps1. It accepts only a
    /// direct harness executable or a dotnet-hosted harness DLL whose command
    /// line includes the exact <c>--run-id</c> value.
    /// </summary>
    public static bool IsC6HarnessCommandLine(string? commandLine, string? runId)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || string.IsNullOrWhiteSpace(runId))
        {
            return false;
        }

        var trimmed = commandLine.Trim();
        var directExe = System.Text.RegularExpressions.Regex.IsMatch(
            trimmed,
            "^(?:\"[^\"]*QuickNotes\\.C6\\.Harness\\.exe\"|\\S*QuickNotes\\.C6\\.Harness\\.exe)(?:\\s|$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var dotnetDll = System.Text.RegularExpressions.Regex.IsMatch(
            trimmed,
            "^(?:\"[^\"]*dotnet(?:\\.exe)?\"|\\S*dotnet(?:\\.exe)?)\\s+(?:\"[^\"]*QuickNotes\\.C6\\.Harness\\.dll\"|\\S*QuickNotes\\.C6\\.Harness\\.dll)(?:\\s|$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!directExe && !dotnetDll)
        {
            return false;
        }

        var runIdPattern = "--run-id\\s+\"?" + System.Text.RegularExpressions.Regex.Escape(runId)
            + "\"?(?:\\s|$)";
        return System.Text.RegularExpressions.Regex.IsMatch(trimmed, runIdPattern);
    }

    public static void KillProcessTree(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (process.HasExited)
        {
            return;
        }

        process.Kill(entireProcessTree: true);
    }

    public static async Task WaitForExitAsync(Process process, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(process);
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"The process did not exit within {timeout}.");
        }
    }

    private static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";
}
