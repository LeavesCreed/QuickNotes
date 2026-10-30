using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace QuickNotes.IntegrationTests.C6;

/// <summary>
/// Safe, real executions of the C6 PowerShell drivers. Every test uses a
/// disposable <c>%TEMP%\QuickNotes-C6\&lt;runId&gt;</c> directory; volume
/// creation is only exercised through its non-destructive validation mode.
/// ACL tests use the current test directory only and require the script to
/// restore the SDDL before it exits.
/// </summary>
public sealed class C6StorageFaultScriptTests
{
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromMinutes(3);

    [Fact]
    public void NewVolume_ValidateOnly_DoesNotCreateAnything()
    {
        var runId = NewRunId();
        try
        {
            var result = RunScriptAllowFailure(
                "New-C6TestVolume.ps1",
                "-RunId", runId,
                "-ValidateOnly");

            Assert.Contains(
                result.Json.GetProperty("status").GetString(),
                new[] { "READY", "NOT RUN" });
            Assert.False(result.Json.GetProperty("createdByC6").GetBoolean());
            Assert.False(File.Exists(Path.Combine(RunRoot(runId), $"{runId}.vhdx")));
            Assert.False(File.Exists(Path.Combine(RunRoot(runId), $"{runId}.volume.json")));
        }
        finally
        {
            TryDeleteRunRoot(runId);
        }
    }

    [Fact]
    public void NewVolume_RejectsUnsafeSize()
    {
        var runId = NewRunId();
        try
        {
            var result = RunScriptAllowFailure(
                "New-C6TestVolume.ps1",
                "-SizeMB", "64",
                "-RunId", runId);

            Assert.NotEqual(0, result.ExitCode);
            Assert.NotEqual("PASS", result.Json.GetProperty("status").GetString());
            Assert.False(result.Json.GetProperty("createdByC6").GetBoolean());
            Assert.False(File.Exists(Path.Combine(RunRoot(runId), $"{runId}.vhdx")));
        }
        finally
        {
            TryDeleteRunRoot(runId);
        }
    }

    [Theory]
    [InlineData(@"..\escape")]
    [InlineData("..")]
    [InlineData("")]
    public void NewVolume_RejectsUnsafeRunId(string runId)
    {
        var result = RunScriptAllowFailure(
            "New-C6TestVolume.ps1",
            "-RunId", runId,
            "-ValidateOnly");

        Assert.NotEqual(0, result.ExitCode);
        Assert.NotEqual("PASS", result.Json.GetProperty("status").GetString());
        Assert.False(result.Json.GetProperty("createdByC6").GetBoolean());
        Assert.False(File.Exists(Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "QuickNotes-C6",
            "..",
            "escape.vhdx"))));
    }

    [Fact]
    public void RemoveVolume_RefusesWhenCreatedByC6IsAbsent()
    {
        var runId = NewRunId();
        var root = RunRoot(runId);
        Directory.CreateDirectory(root);
        var vhdx = Path.Combine(root, $"{runId}.vhdx");
        File.WriteAllText(vhdx, "not a volume");
        File.WriteAllText(
            Path.Combine(root, $"{runId}.volume.json"),
            JsonSerializer.Serialize(new { RunId = runId, CreatedByC6 = false, VhdxPath = vhdx }));
        try
        {
            var result = RunScriptAllowFailure("Remove-C6TestVolume.ps1", "-RunId", runId);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Equal("REFUSED", result.Json.GetProperty("status").GetString());
            Assert.True(File.Exists(vhdx));
        }
        finally
        {
            TryDeleteRunRoot(runId);
        }
    }

    [Fact]
    public void AclDraftCase_RunsRealFaultRestoresAclAndPasses()
    {
        var runId = NewRunId();
        try
        {
            var result = RunScript(
                "Invoke-C6AclFaults.ps1",
                "-RunId", runId,
                "-Case", "DraftDeny",
                "-Notes", "5");

            Assert.Equal(0, result.ExitCode);
            var caseResult = ReadCaseResult(runId, "C6-F02");
            Assert.Equal("PASS", caseResult.GetProperty("status").GetString());
            Assert.True(caseResult.GetProperty("actual").GetProperty("draftFailureVisible").GetBoolean());
            Assert.True(caseResult.GetProperty("actual").GetProperty("retrySucceeded").GetBoolean());
            Assert.True(caseResult.GetProperty("cleanup").GetProperty("aclRestored").GetBoolean());
            Assert.True(caseResult.GetProperty("restart").GetProperty("passed").GetBoolean());

            var drafts = Path.Combine(RunRoot(runId), "acl-root", "drafts");
            Assert.True(CanWrite(drafts), "The draft directory ACL was not restored.");
            Assert.Empty(Directory.GetFiles(RunRoot(runId), "acl-*.json", SearchOption.AllDirectories));
        }
        finally
        {
            TryClear(runId);
        }
    }

    [Fact]
    public void AclBackupCase_KeepsExistingBackupAndRestoresAcl()
    {
        var runId = NewRunId();
        try
        {
            var result = RunScript(
                "Invoke-C6AclFaults.ps1",
                "-RunId", runId,
                "-Case", "BackupDeny",
                "-Notes", "5");

            Assert.Equal(0, result.ExitCode);
            var caseResult = ReadCaseResult(runId, "C6-F03");
            Assert.Equal("PASS", caseResult.GetProperty("status").GetString());
            Assert.True(caseResult.GetProperty("actual").GetProperty("noFalseSuccess").GetBoolean());
            Assert.True(caseResult.GetProperty("actual").GetProperty("existingBackupIntact").GetBoolean());
            Assert.True(caseResult.GetProperty("cleanup").GetProperty("aclRestored").GetBoolean());
            Assert.True(caseResult.GetProperty("restart").GetProperty("passed").GetBoolean());

            var backups = Path.Combine(RunRoot(runId), "acl-root", "backups");
            Assert.True(CanWrite(backups), "The backup directory ACL was not restored.");
            Assert.Empty(Directory.GetFiles(RunRoot(runId), "acl-*.json", SearchOption.AllDirectories));
            Assert.NotEmpty(Directory.GetFiles(backups, "quicknotes-*.zip"));
        }
        finally
        {
            TryClear(runId);
        }
    }

    [Fact]
    public void StorageFaults_MigrationWritesANotRunCase()
    {
        var runId = NewRunId();
        try
        {
            var result = RunScript(
                "Invoke-C6StorageFaults.ps1",
                "-RunId", runId,
                "-Scenario", "migration",
                "-Notes", "5");

            Assert.Equal(0, result.ExitCode);
            var caseResult = ReadCaseResult(runId, "C6-F04");
            Assert.Equal("NOT RUN", caseResult.GetProperty("status").GetString());
            var reason = caseResult.GetProperty("reason").GetString();
            Assert.Contains("migration", reason, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(RunRoot(runId), $"{runId}.vhdx")));
        }
        finally
        {
            TryClear(runId);
        }
    }

    [Fact]
    public void ClearArtifacts_RemovesOnlyTheTaggedRunRoot()
    {
        var runId = NewRunId();
        var sibling = RunRoot(NewRunId());
        var root = RunRoot(runId);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "keep.txt"), "keep");
        var vhdx = Path.Combine(root, $"{runId}.vhdx");
        File.WriteAllText(vhdx, "not a volume");
        File.WriteAllText(
            Path.Combine(root, $"{runId}.volume.json"),
            JsonSerializer.Serialize(new
            {
                RunId = runId,
                CreatedByC6 = true,
                VhdxPath = vhdx,
                MountPath = Path.Combine(root, "volume")
            }));
        try
        {
            var result = RunScript("Clear-C6Artifacts.ps1", "-RunId", runId);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("PASS", result.Json.GetProperty("status").GetString());
            Assert.False(Directory.Exists(root));
            Assert.True(File.Exists(Path.Combine(sibling, "keep.txt")));
        }
        finally
        {
            TryDeleteRunRoot(runId);
            TryDeleteRunRoot(Path.GetFileName(sibling));
        }
    }

    [Fact]
    public void ClearArtifacts_RejectsUnsafeRunId()
    {
        var result = RunScriptAllowFailure("Clear-C6Artifacts.ps1", "-RunId", @"..\escape");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("REFUSED", result.Json.GetProperty("status").GetString());
    }

    private static string NewRunId() => "c6-test-" + Guid.NewGuid().ToString("N")[..12];

    private static string HandoffRoot()
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

    private static string RunRoot(string runId) =>
        Path.Combine(Path.GetTempPath(), "QuickNotes-C6", runId);

    private static ScriptResult RunScript(string script, params string[] arguments)
    {
        var result = RunScriptAllowFailure(script, arguments);
        Assert.True(
            result.ExitCode == 0,
            $"Script {script} failed with exit {result.ExitCode}.{Environment.NewLine}{result.StandardError}{Environment.NewLine}{result.StandardOutput}");
        return result;
    }

    private static ScriptResult RunScriptAllowFailure(string script, params string[] arguments)
    {
        var scriptPath = Path.Combine(HandoffRoot(), "scripts", "c6", script);
        Assert.True(File.Exists(scriptPath), $"Missing script {scriptPath}.");

        var startInfo = CreateStartInfo();
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {script}.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)ScriptTimeout.TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"Script {script} exceeded {ScriptTimeout}.");
        }

        var output = standardOutput.GetAwaiter().GetResult();
        var error = standardError.GetAwaiter().GetResult();
        var jsonLine = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => line.StartsWith('{'));
        if (jsonLine is null)
        {
            throw new InvalidOperationException(
                $"Script {script} did not print a JSON result.{Environment.NewLine}{output}{Environment.NewLine}{error}");
        }

        using var document = JsonDocument.Parse(jsonLine);
        return new ScriptResult(process.ExitCode, output, error, document.RootElement.Clone());
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

    private static JsonElement ReadCaseResult(string runId, string caseName)
    {
        var path = Path.Combine(RunRoot(runId), "results", $"{caseName}.result.json");
        Assert.True(File.Exists(path), $"Missing case result {path}.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    private static bool CanWrite(string directory)
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

    private static void TryClear(string runId)
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

    private static void TryDeleteRunRoot(string runId)
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

    private sealed record ScriptResult(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        JsonElement Json);
}
