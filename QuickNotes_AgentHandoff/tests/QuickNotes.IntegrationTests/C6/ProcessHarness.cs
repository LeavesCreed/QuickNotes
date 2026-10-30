using System.Diagnostics;
using System.Text;

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
