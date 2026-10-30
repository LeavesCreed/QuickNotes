using System.Security.Cryptography;
using static QuickNotes.IntegrationTests.C6.C6ScriptTestSupport;

namespace QuickNotes.IntegrationTests.C6;

/// <summary>
/// Opt-in C6 cases that execute real ACL changes against disposable
/// <c>%TEMP%\QuickNotes-C6\&lt;runId&gt;</c> directories. They are statically
/// skipped so the default <c>dotnet test</c> run never applies an ACL or
/// creates a VHD. Run them only through the explicit driver, for example:
/// <c>pwsh -File scripts/c6/Invoke-C6AclFaults.ps1 -RunId c6-task2 -Case All
/// -Notes 1000 -EvidenceRoot .superpowers/sdd/.../task-2-evidence/c6-task2</c>.
/// </summary>
public sealed class C6RealFaultScriptTests
{
    private const string SkipReason =
        "Opt-in only: applies a real temporary ACL and must never run in the default dotnet test suite. " +
        "Run the explicit driver: pwsh -File scripts/c6/Invoke-C6AclFaults.ps1 -RunId <id> -Case All " +
        "-Notes 1000 -EvidenceRoot <retained evidence directory>.";

    [Fact(Skip = SkipReason)]
    [Trait("Category", "C6RealFault")]
    public void AclDraftCase_RunsRealFaultRestoresAclAndPasses()
    {
        var runId = NewRunId();
        try
        {
            var retained = Path.Combine(Path.GetTempPath(), "c6-real-evidence-" + runId);
            var result = RunScript(
                "Invoke-C6AclFaults.ps1",
                "-RunId", runId,
                "-Case", "DraftDeny",
                "-Notes", "5",
                "-EvidenceRoot", retained);

            Assert.Equal(0, result.ExitCode);
            var caseResult = ReadCaseResult(runId, "C6-F02");
            var actual = caseResult.GetProperty("actual");
            var cleanup = caseResult.GetProperty("cleanup");

            Assert.Equal("PASS", caseResult.GetProperty("status").GetString());
            Assert.True(actual.GetProperty("draftFailureVisible").GetBoolean());
            Assert.True(actual.GetProperty("retrySucceeded").GetBoolean());
            Assert.True(actual.GetProperty("firstRestartPassed").GetBoolean());
            Assert.True(actual.GetProperty("retryRestartPassed").GetBoolean());
            Assert.True(actual.GetProperty("aclMask").GetProperty("denyAcePresent").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(
                actual.GetProperty("aclMask").GetProperty("denyMaskHex").GetString()));
            Assert.True(cleanup.GetProperty("aclRestored").GetBoolean());
            Assert.True(cleanup.GetProperty("sddlMatchesOriginal").GetBoolean());
            Assert.True(cleanup.GetProperty("writeProbePassed").GetBoolean());

            var drafts = Path.Combine(RunRoot(runId), "acl-root", "drafts");
            Assert.True(CanWrite(drafts), "The draft directory ACL was not restored.");
            Assert.Empty(Directory.GetFiles(RunRoot(runId), "acl-*.json", SearchOption.AllDirectories));
            AssertAllEvidenceExists(caseResult);
        }
        finally
        {
            TryClear(runId);
        }
    }

    [Fact(Skip = SkipReason)]
    [Trait("Category", "C6RealFault")]
    public void AclBackupCase_KeepsExistingBackupAndRestoresAcl()
    {
        var runId = NewRunId();
        try
        {
            var retained = Path.Combine(Path.GetTempPath(), "c6-real-evidence-" + runId);
            var result = RunScript(
                "Invoke-C6AclFaults.ps1",
                "-RunId", runId,
                "-Case", "BackupDeny",
                "-Notes", "5",
                "-EvidenceRoot", retained);

            Assert.Equal(0, result.ExitCode);
            var caseResult = ReadCaseResult(runId, "C6-F03");
            var actual = caseResult.GetProperty("actual");
            var cleanup = caseResult.GetProperty("cleanup");

            Assert.Equal("PASS", caseResult.GetProperty("status").GetString());
            Assert.True(actual.GetProperty("noFalseSuccess").GetBoolean());
            Assert.True(actual.GetProperty("existingBackupIntact").GetBoolean());
            Assert.True(actual.GetProperty("faultSignature").GetProperty("exactMatch").GetBoolean());
            Assert.Equal(
                "StorageUnavailable",
                actual.GetProperty("faultSignature").GetProperty("observedStatus").GetString());
            Assert.Equal(
                "DestinationUnavailable",
                actual.GetProperty("faultSignature").GetProperty("observedErrorCode").GetString());
            Assert.True(cleanup.GetProperty("aclRestored").GetBoolean());
            Assert.True(cleanup.GetProperty("sddlMatchesOriginal").GetBoolean());
            Assert.True(cleanup.GetProperty("writeProbePassed").GetBoolean());
            Assert.True(actual.GetProperty("aclMask").GetProperty("denyAcePresent").GetBoolean());

            var backups = Path.Combine(RunRoot(runId), "acl-root", "backups");
            Assert.True(CanWrite(backups), "The backup directory ACL was not restored.");
            Assert.Empty(Directory.GetFiles(RunRoot(runId), "acl-*.json", SearchOption.AllDirectories));
            Assert.NotEmpty(Directory.GetFiles(backups, "quicknotes-*.zip"));

            // Independently rehash the package captured during the baseline run
            // instead of trusting only the script's existingBackupIntact flag.
            var baselinePackages = actual.GetProperty("baseline").GetProperty("packages").EnumerateArray().ToList();
            Assert.NotEmpty(baselinePackages);
            foreach (var package in baselinePackages)
            {
                var name = package.GetProperty("name").GetString();
                var expectedHash = package.GetProperty("sha256").GetString();
                var path = Path.Combine(backups, name!);
                Assert.True(File.Exists(path), $"Baseline package {name} is missing.");
                var actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
                Assert.Equal(expectedHash, actualHash, ignoreCase: true);
            }

            AssertAllEvidenceExists(caseResult);
        }
        finally
        {
            TryClear(runId);
        }
    }

    [Fact(Skip = SkipReason)]
    [Trait("Category", "C6RealFault")]
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
            Assert.False(File.Exists(Path.Combine(
                Path.GetTempPath(),
                "QuickNotes-C6",
                $"{runId}.vhdx")));
        }
        finally
        {
            TryClear(runId);
        }
    }

    private static void AssertAllEvidenceExists(System.Text.Json.JsonElement caseResult)
    {
        Assert.True(
            File.Exists(caseResult.GetProperty("resultArchivePath").GetString()),
            "The retained result archive is missing.");
        foreach (var evidence in caseResult.GetProperty("evidence").EnumerateArray())
        {
            var path = evidence.GetString();
            Assert.False(string.IsNullOrWhiteSpace(path));
            Assert.True(File.Exists(path), $"Retained evidence file is missing: {path}");
        }
    }
}
