using System.Diagnostics;
using System.Text.Json;
using static QuickNotes.IntegrationTests.C6.C6ScriptTestSupport;

namespace QuickNotes.IntegrationTests.C6;

/// <summary>
/// Safe C6 script contracts for the default suite. These tests either inspect
/// the scripts or exercise refusal/non-destructive paths. They never invoke
/// Invoke-C6AclFaults.ps1, apply an ACL, or create/mount a VHD. Real fault
/// driver cases live in C6RealFaultScriptTests and are statically skipped.
/// </summary>
public sealed class C6StorageFaultScriptTests
{
    [Fact]
    public void Scripts_ExistAndParse()
    {
        var scripts = Directory.GetFiles(ScriptsRoot(), "*.ps1");
        Assert.NotEmpty(scripts);

        var command = string.Join(
            Environment.NewLine,
            "$errors = @()",
            $"foreach ($file in @({string.Join(", ", scripts.Select(PsQuote))})) {{",
            "  $tokens = $null; $parseErrors = $null",
            "  [System.Management.Automation.Language.Parser]::ParseFile($file, [ref]$tokens, [ref]$parseErrors) | Out-Null",
            "  if ($null -ne $parseErrors -and $parseErrors.Count -gt 0) {",
            "    $errors += ($file + ': ' + ($parseErrors -join '; '))",
            "  }",
            "}",
            "if ($errors.Count -gt 0) { throw ($errors -join ' | ') }",
            "'PARSE_OK'");

        var result = RunPowerShellCommand(command);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("PARSE_OK", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void Scripts_DeclareRequiredSafetyContracts()
    {
        var common = File.ReadAllText(Path.Combine(ScriptsRoot(), "C6ScriptCommon.ps1"));
        var acl = File.ReadAllText(Path.Combine(ScriptsRoot(), "Invoke-C6AclFaults.ps1"));
        var storage = File.ReadAllText(Path.Combine(ScriptsRoot(), "Invoke-C6StorageFaults.ps1"));
        var newVolume = File.ReadAllText(Path.Combine(ScriptsRoot(), "New-C6TestVolume.ps1"));
        var clear = File.ReadAllText(Path.Combine(ScriptsRoot(), "Clear-C6Artifacts.ps1"));

        Assert.Contains("Get-Volume -FilePath", common, StringComparison.Ordinal);
        Assert.Contains("Test-C6MountIdentity", common, StringComparison.Ordinal);
        Assert.Contains("Test-C6BackupFaultSignature", common, StringComparison.Ordinal);
        Assert.Contains("Get-C6ReparsePoints", common, StringComparison.Ordinal);
        Assert.Contains(".superpowers", common, StringComparison.Ordinal);

        Assert.Contains("Test-C6BackupFaultSignature", acl, StringComparison.Ordinal);
        Assert.Contains("$faultSignature.Passed", acl, StringComparison.Ordinal);
        Assert.Contains("firstRestartPassed", acl, StringComparison.Ordinal);
        Assert.Contains("sddlMatchesOriginal", acl, StringComparison.Ordinal);

        Assert.Contains("Get-C6MountVerification", storage, StringComparison.Ordinal);
        Assert.Contains("mountVerified", storage, StringComparison.Ordinal);
        Assert.Contains("Get-C6MountVerification", newVolume, StringComparison.Ordinal);
        Assert.Contains("mountVerified", newVolume, StringComparison.Ordinal);

        Assert.Contains("Get-C6ReparsePoints", clear, StringComparison.Ordinal);
        Assert.Contains("partialVhdxRemoved", clear, StringComparison.Ordinal);
        Assert.Contains("partialMountVerified", clear, StringComparison.Ordinal);
    }

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
    public void ClearArtifacts_RemovesOnlyTheTaggedRunRoot()
    {
        var runId = NewRunId();
        var sibling = RunRoot(NewRunId());
        var root = RunRoot(runId);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "keep.txt"), "keep");
        var vhdx = Path.Combine(Path.GetTempPath(), "QuickNotes-C6", $"{runId}.vhdx");
        File.WriteAllText(vhdx, "not a volume");
        File.WriteAllText(
            Path.Combine(Path.GetTempPath(), "QuickNotes-C6", $"{runId}.volume.json"),
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
            Assert.False(File.Exists(vhdx));
            Assert.True(File.Exists(Path.Combine(sibling, "keep.txt")));
        }
        finally
        {
            TryDeleteRunRoot(runId);
            TryDeleteRunRoot(Path.GetFileName(sibling));
            TryDeleteVolumeFiles(runId);
        }
    }

    [Fact]
    public void ClearArtifacts_RemovesExactPartialVhdxWithoutMetadata()
    {
        var runId = NewRunId();
        var root = RunRoot(runId);
        Directory.CreateDirectory(root);
        var vhdx = Path.Combine(Path.GetTempPath(), "QuickNotes-C6", $"{runId}.vhdx");
        File.WriteAllText(vhdx, "partial creation without metadata");
        try
        {
            var result = RunScript("Clear-C6Artifacts.ps1", "-RunId", runId);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("PASS", result.Json.GetProperty("status").GetString());
            Assert.True(result.Json.GetProperty("detail").GetProperty("partialVhdxRemoved").GetBoolean());
            Assert.False(File.Exists(vhdx));
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            TryDeleteRunRoot(runId);
            TryDeleteVolumeFiles(runId);
        }
    }

    [Fact]
    public void ClearArtifacts_RefusesReparsePointUnderRunRoot()
    {
        var runId = NewRunId();
        var root = RunRoot(runId);
        var target = Path.Combine(Path.GetTempPath(), "c6-reparse-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "keep");
        var junction = Path.Combine(root, "junction");
        try
        {
            Assert.True(
                TryCreateJunction(junction, target),
                $"Could not create a test junction from {junction} to {target}.");

            var result = RunScriptAllowFailure("Clear-C6Artifacts.ps1", "-RunId", runId);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Equal("REFUSED", result.Json.GetProperty("status").GetString());
            Assert.True(Directory.Exists(root), "The run root must be kept when a reparse point is refused.");
            Assert.True(File.Exists(Path.Combine(target, "keep.txt")), "The junction target must not be deleted.");
        }
        finally
        {
            TryDeleteJunction(junction);
            TryDeleteRunRoot(runId);
            TryDeleteDirectory(target);
        }
    }

    [Fact]
    public void ClearArtifacts_RejectsUnsafeRunId()
    {
        var result = RunScriptAllowFailure("Clear-C6Artifacts.ps1", "-RunId", @"..\escape");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("REFUSED", result.Json.GetProperty("status").GetString());
    }

    [Fact]
    public void BackupFaultSignature_RequiresExactStatusAndErrorCode()
    {
        var common = Path.Combine(ScriptsRoot(), "C6ScriptCommon.ps1");
        var command = string.Join(
            Environment.NewLine,
            "$ErrorActionPreference = 'Stop'",
            $". {PsQuote(common)}",
            "$good = [pscustomobject]@{ backup = [pscustomobject]@{ status = 'StorageUnavailable'; errorCode = 'DestinationUnavailable' } }",
            "$wrongStatus = [pscustomobject]@{ backup = [pscustomobject]@{ status = 'Succeeded'; errorCode = 'DestinationUnavailable' } }",
            "$wrongCode = [pscustomobject]@{ backup = [pscustomobject]@{ status = 'StorageUnavailable'; errorCode = 'IoError' } }",
            "$missing = [pscustomobject]@{}",
            "@{",
            "  good = (Test-C6BackupFaultSignature -FaultedJson $good).Passed",
            "  wrongStatus = (Test-C6BackupFaultSignature -FaultedJson $wrongStatus).Passed",
            "  wrongCode = (Test-C6BackupFaultSignature -FaultedJson $wrongCode).Passed",
            "  missing = (Test-C6BackupFaultSignature -FaultedJson $missing).Passed",
            "} | ConvertTo-Json -Compress");

        var result = RunPowerShellCommand(command);
        Assert.Equal(0, result.ExitCode);

        var json = ParseLastJsonObject(result.StandardOutput);
        Assert.True(json.GetProperty("good").GetBoolean());
        Assert.False(json.GetProperty("wrongStatus").GetBoolean());
        Assert.False(json.GetProperty("wrongCode").GetBoolean());
        Assert.False(json.GetProperty("missing").GetBoolean());
    }

    [Fact]
    public void MountIdentity_RequiresMatchingVolumePartitionAndNonSystemPath()
    {
        var common = Path.Combine(ScriptsRoot(), "C6ScriptCommon.ps1");
        var volumeId = "11111111-1111-1111-1111-111111111111";
        var runRoot = @"C:\Users\c6\AppData\Local\Temp\QuickNotes-C6\c6-contract";
        var mountPath = runRoot + @"\volume";
        var command = string.Join(
            Environment.NewLine,
            "$ErrorActionPreference = 'Stop'",
            $". {PsQuote(common)}",
            $"$volume = [pscustomobject]@{{ FileSystemLabel = 'C6-c6-contract'; DriveLetter = $null; Path = '\\\\?\\Volume{{{volumeId}}}\\'; UniqueId = '{volumeId}' }}",
            $"$partition = [pscustomobject]@{{ DiskNumber = 7; PartitionNumber = 2; Guid = '{volumeId}' }}",
            $"$good = Test-C6MountIdentity -ResolvedVolume $volume -ResolvedPartition $partition -ExpectedDiskNumber 7 -ExpectedPartitionNumber 2 -ExpectedVolumeId '{volumeId}' -ExpectedLabel 'C6-c6-contract' -MountPath {PsQuote(mountPath)} -RunRoot {PsQuote(runRoot)} -SystemRoot 'C:\\Windows'",
            "$badLabel = Test-C6MountIdentity -ResolvedVolume $volume -ResolvedPartition $partition -ExpectedDiskNumber 7 -ExpectedPartitionNumber 2 -ExpectedVolumeId '" + volumeId + "' -ExpectedLabel 'C6-other' -MountPath " + PsQuote(mountPath) + " -RunRoot " + PsQuote(runRoot) + " -SystemRoot 'C:\\Windows'",
            "$badDisk = Test-C6MountIdentity -ResolvedVolume $volume -ResolvedPartition $partition -ExpectedDiskNumber 8 -ExpectedPartitionNumber 2 -ExpectedVolumeId '" + volumeId + "' -ExpectedLabel 'C6-c6-contract' -MountPath " + PsQuote(mountPath) + " -RunRoot " + PsQuote(runRoot) + " -SystemRoot 'C:\\Windows'",
            "$systemVolume = [pscustomobject]@{ FileSystemLabel = 'Windows'; DriveLetter = 'C'; Path = '\\\\?\\Volume{22222222-2222-2222-2222-222222222222}\\'; UniqueId = '22222222-2222-2222-2222-222222222222' }",
            "$systemPartition = [pscustomobject]@{ DiskNumber = 0; PartitionNumber = 1; Guid = '22222222-2222-2222-2222-222222222222' }",
            "$badSystem = Test-C6MountIdentity -ResolvedVolume $systemVolume -ResolvedPartition $systemPartition -ExpectedDiskNumber 0 -ExpectedPartitionNumber 1 -ExpectedVolumeId '22222222-2222-2222-2222-222222222222' -ExpectedLabel 'Windows' -MountPath 'C:\\Windows\\Temp\\volume' -RunRoot 'C:\\Windows\\Temp' -SystemRoot 'C:\\Windows'",
            "@{",
            "  good = $good.Passed",
            "  badLabel = $badLabel.Passed",
            "  badDisk = $badDisk.Passed",
            "  badSystem = $badSystem.Passed",
            "  systemFailures = @($badSystem.Failures)",
            "} | ConvertTo-Json -Compress");

        var result = RunPowerShellCommand(command);
        Assert.Equal(0, result.ExitCode);

        var json = ParseLastJsonObject(result.StandardOutput);
        Assert.True(json.GetProperty("good").GetBoolean());
        Assert.False(json.GetProperty("badLabel").GetBoolean());
        Assert.False(json.GetProperty("badDisk").GetBoolean());
        Assert.False(json.GetProperty("badSystem").GetBoolean());
        Assert.Contains(
            json.GetProperty("systemFailures").EnumerateArray().Select(item => item.GetString()),
            failure => failure is not null && failure.Contains("system volume", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EvidenceRoot_IsResolvedOutsideTheDisposableRunRoot()
    {
        var runId = NewRunId();
        var retained = Path.Combine(
            Path.GetTempPath(),
            "c6-contract-evidence-" + Guid.NewGuid().ToString("N"));
        var common = Path.Combine(ScriptsRoot(), "C6ScriptCommon.ps1");
        var command = string.Join(
            Environment.NewLine,
            "$ErrorActionPreference = 'Stop'",
            $". {PsQuote(common)}",
            $"$out = Get-C6EvidenceRoot -HandoffRoot {PsQuote(HandoffRoot())} -RunId '{runId}' -EvidenceRoot {PsQuote(retained)}",
            $"$runRoot = Get-C6RunRoot -RunId '{runId}'",
            "@{",
            "  path = $out",
            "  underRunRoot = (Test-C6PathUnder -Path $out -Root $runRoot)",
            "  exists = (Test-Path -LiteralPath $out -PathType Container)",
            "} | ConvertTo-Json -Compress");
        try
        {
            var result = RunPowerShellCommand(command);
            Assert.Equal(0, result.ExitCode);

            var json = ParseLastJsonObject(result.StandardOutput);
            Assert.False(json.GetProperty("underRunRoot").GetBoolean());
            Assert.True(json.GetProperty("exists").GetBoolean());
            Assert.Equal(retained, json.GetProperty("path").GetString(), ignoreCase: true);
        }
        finally
        {
            TryDeleteDirectory(retained);
        }
    }

    private static bool TryCreateJunction(string linkPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(linkPath);
        startInfo.ArgumentList.Add(targetPath);
        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return false;
        }

        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);
        return process.HasExited && process.ExitCode == 0;
    }

    private static void TryDeleteJunction(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("rmdir");
        startInfo.ArgumentList.Add(path);
        using var process = Process.Start(startInfo);
        process?.WaitForExit(30_000);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteVolumeFiles(string runId)
    {
        var root = Path.Combine(Path.GetTempPath(), "QuickNotes-C6");
        foreach (var path in new[]
                 {
                     Path.Combine(root, $"{runId}.vhdx"),
                     Path.Combine(root, $"{runId}.volume.json")
                 })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
