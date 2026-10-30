using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace QuickNotes.IntegrationTests.C6;

/// <summary>
/// Shared process helpers for the C6 script contract tests. Nothing here
/// creates a VHD or changes an ACL by itself; callers decide whether a test is
/// safe for the default suite or belongs to the opt-in real-fault class.
/// </summary>
internal static class C6ScriptTestSupport
{
    public static readonly TimeSpan ScriptTimeout = TimeSpan.FromMinutes(3);

    public static string NewRunId() => "c6-test-" + Guid.NewGuid().ToString("N")[..12];

    public static string HandoffRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "QuickNotes.DataPrototype.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the handoff root.");
    }

    public static string ScriptsRoot() => Path.Combine(HandoffRoot(), "scripts", "c6");

    public static string RunRoot(string runId) =>
        Path.Combine(Path.GetTempPath(), "QuickNotes-C6", runId);

    public static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";

    public static ScriptResult RunScript(string script, params string[] arguments)
    {
        var result = RunScriptAllowFailure(script, arguments);
        Assert.True(
            result.ExitCode == 0,
            $"Script {script} failed with exit {result.ExitCode}.{Environment.NewLine}{result.StandardError}{Environment.NewLine}{result.StandardOutput}");
        return result;
    }

    public static ScriptResult RunScriptAllowFailure(string script, params string[] arguments)
    {
        var scriptPath = Path.Combine(ScriptsRoot(), script);
        Assert.True(File.Exists(scriptPath), $"Missing script {scriptPath}.");

        var startInfo = CreateStartInfo();
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var (exitCode, output, error) = RunProcess(startInfo, script);
        var jsonLine = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => line.StartsWith('{'));
        if (jsonLine is null)
        {
            throw new InvalidOperationException(
                $"Script {script} did not print a JSON result.{Environment.NewLine}{output}{Environment.NewLine}{error}");
        }

        using var document = JsonDocument.Parse(jsonLine);
        return new ScriptResult(exitCode, output, error, document.RootElement.Clone());
    }

    public static CommandResult RunPowerShellCommand(string command)
    {
        var startInfo = CreateStartInfo();
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);
        var (exitCode, output, error) = RunProcess(startInfo, "PowerShell command");
        return new CommandResult(exitCode, output, error);
    }

    public static JsonElement ParseLastJsonObject(string output)
    {
        var jsonLine = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => line.StartsWith('{'));
        Assert.False(
            jsonLine is null,
            $"The PowerShell command did not print a JSON object.{Environment.NewLine}{output}");
        using var document = JsonDocument.Parse(jsonLine!);
        return document.RootElement.Clone();
    }

    public static JsonElement ReadCaseResult(string runId, string caseName)
    {
        var path = Path.Combine(RunRoot(runId), "results", $"{caseName}.result.json");
        Assert.True(File.Exists(path), $"Missing case result {path}.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    public static bool CanWrite(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }

        var probe = Path.Combine(directory, $".write-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void TryClear(string runId)
    {
        try
        {
            RunScriptAllowFailure("Clear-C6Artifacts.ps1", "-RunId", runId);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or TimeoutException)
        {
        }

        TryDeleteRunRoot(runId);
    }

    public static void TryDeleteRunRoot(string runId)
    {
        try
        {
            var path = RunRoot(runId);
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static ProcessStartInfo CreateStartInfo()
    {
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
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        return startInfo;
    }

    private static (int ExitCode, string Output, string Error) RunProcess(
        ProcessStartInfo startInfo,
        string description)
    {
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {description}.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)ScriptTimeout.TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{description} exceeded {ScriptTimeout}.");
        }

        return (
            process.ExitCode,
            standardOutput.GetAwaiter().GetResult(),
            standardError.GetAwaiter().GetResult());
    }

    internal sealed record ScriptResult(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        JsonElement Json);

    internal sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);
}
