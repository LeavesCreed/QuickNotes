using System.Text.Json;
using static QuickNotes.IntegrationTests.C6.C6ScriptTestSupport;

namespace QuickNotes.IntegrationTests.C6;

/// <summary>
/// Safe contracts for the C6 Task 5 performance driver. These tests never
/// launch WPF or run a soak; they inspect the script and exercise the pure
/// soak/command helpers plus the confirmed-shape refusal path.
/// </summary>
public sealed class C6PerformanceScriptTests
{
    [Fact]
    public void PerformanceScript_DeclaresHardenedAcceptanceAndSoakContracts()
    {
        var script = File.ReadAllText(Path.Combine(ScriptsRoot(), "Invoke-C6Performance.ps1"));
        var common = File.ReadAllText(Path.Combine(ScriptsRoot(), "C6ScriptCommon.ps1"));

        Assert.Contains("Notes -lt 1000", script, StringComparison.Ordinal);
        Assert.Contains("Visible -lt 20", script, StringComparison.Ordinal);
        Assert.Contains("StartupIterations -lt 10", script, StringComparison.Ordinal);
        Assert.Contains("UiLaunches -lt 10", script, StringComparison.Ordinal);
        Assert.Contains("requiredWindowCounts = @(5, 10, 20)", script, StringComparison.Ordinal);
        Assert.Contains("soak_sample_count_shortfall", script, StringComparison.Ordinal);
        Assert.Contains("soak_cadence_max_gap_seconds", script, StringComparison.Ordinal);
        Assert.Contains("soak_sample_interval_seconds", script, StringComparison.Ordinal);
        Assert.Contains("soak_edit_save_failures", script, StringComparison.Ordinal);
        Assert.Contains("soak_working_set_growth_percent", script, StringComparison.Ordinal);
        Assert.Contains("soak_handle_growth", script, StringComparison.Ordinal);
        Assert.Contains("soak_process_unresponsive", script, StringComparison.Ordinal);
        Assert.Contains("soak_unexpected_windows", script, StringComparison.Ordinal);
        Assert.Contains("service_status_coupling", script, StringComparison.Ordinal);
        Assert.Contains("New-C6TwoHourSoakCommand", script, StringComparison.Ordinal);
        Assert.Contains("noteCount", script, StringComparison.Ordinal);
        Assert.Contains(
            "'startup_ui_host_p95' ([double]$hostP95) ([double]$budget.startupReadyP95Ms) 'ms' $false -Diagnostic $true -ThresholdConfirmed $false",
            script,
            StringComparison.Ordinal);

        Assert.Contains(
            "The 2-hour soak requires a 60 s sample cadence",
            common,
            StringComparison.Ordinal);
        Assert.Contains(
            "$minimumSampleCount = if ($isTwoHourAcceptance)",
            common,
            StringComparison.Ordinal);
        Assert.Contains(
            "[Math]::Max(120, $expectedSampleCount - 1)",
            common,
            StringComparison.Ordinal);
        Assert.Contains(
            "$cadenceLimitSeconds = if ($isTwoHourAcceptance) { 90.0 }",
            common,
            StringComparison.Ordinal);
        Assert.Contains("median-first-5-vs-median-last-5", common, StringComparison.Ordinal);
        Assert.Contains("growthThresholdsConfirmed = $false", common, StringComparison.Ordinal);
        Assert.Contains("maximumWorkingSetGrowthPercent = 15", common, StringComparison.Ordinal);
        Assert.Contains("maximumHandleGrowth = 50", common, StringComparison.Ordinal);
    }

    [Fact]
    public void SoakEvidence_SingleSampleCannotPassAndCadenceWindowRulesApply()
    {
        var common = Path.Combine(ScriptsRoot(), "C6ScriptCommon.ps1");
        var command = string.Join(
            Environment.NewLine,
            "$ErrorActionPreference = 'Stop'",
            $". {PsQuote(common)}",
            "$one = @([pscustomobject]@{ elapsedSeconds = 0.2; workingSetBytes = [int64]100000000; handleCount = 500; windowCount = 20; processResponding = $true; unexpectedWindowCount = 0; editSaveSucceeded = $true })",
            "$single = Measure-C6SoakEvidence -Samples $one -DurationSeconds 7200 -SampleSeconds 60 -ExpectedWindowCount 20 -EvaluateGrowth $true",
            "$valid = @(for ($i = 0; $i -le 120; $i++) { [pscustomobject]@{ elapsedSeconds = [double]($i * 60); workingSetBytes = [int64](100000000 + ($i * 100000)); handleCount = 900; windowCount = 20; processResponding = $true; unexpectedWindowCount = 0; editSaveSucceeded = $true } })",
            "$full = Measure-C6SoakEvidence -Samples $valid -DurationSeconds 7200 -SampleSeconds 60 -ExpectedWindowCount 20 -EvaluateGrowth $true",
            "$gapped = @($valid)",
            "$gapped[60] = [pscustomobject]@{ elapsedSeconds = 3660.0; workingSetBytes = [int64]106000000; handleCount = 900; windowCount = 20; processResponding = $true; unexpectedWindowCount = 0; editSaveSucceeded = $true }",
            "$gap = Measure-C6SoakEvidence -Samples $gapped -DurationSeconds 7200 -SampleSeconds 60 -ExpectedWindowCount 20 -EvaluateGrowth $false",
            "$badWindow = @($valid)",
            "$badWindow[0] = [pscustomobject]@{ elapsedSeconds = 0.0; workingSetBytes = [int64]100000000; handleCount = 900; windowCount = 19; processResponding = $false; unexpectedWindowCount = 2; editSaveSucceeded = $true }",
            "$window = Measure-C6SoakEvidence -Samples $badWindow -DurationSeconds 7200 -SampleSeconds 60 -ExpectedWindowCount 20 -EvaluateGrowth $false",
            "$noEdit = @($valid)",
            "$noEdit[0] = [pscustomobject]@{ elapsedSeconds = 0.0; workingSetBytes = [int64]100000000; handleCount = 900; windowCount = 20; processResponding = $true; unexpectedWindowCount = 0 }",
            "$edit = Measure-C6SoakEvidence -Samples $noEdit -DurationSeconds 7200 -SampleSeconds 60 -ExpectedWindowCount 20 -EvaluateGrowth $false",
            "$highGrowth = @(for ($i = 0; $i -le 120; $i++) { [pscustomobject]@{ elapsedSeconds = [double]($i * 60); workingSetBytes = [int64](100000000 + ($i * 150000)); handleCount = 900; windowCount = 20; processResponding = $true; unexpectedWindowCount = 0; editSaveSucceeded = $true } })",
            "$high = Measure-C6SoakEvidence -Samples $highGrowth -DurationSeconds 7200 -SampleSeconds 60 -ExpectedWindowCount 20 -EvaluateGrowth $true",
            "$coarse = @(for ($i = 0; $i -le 12; $i++) { [pscustomobject]@{ elapsedSeconds = [double]($i * 600); workingSetBytes = [int64]100000000; handleCount = 900; windowCount = 20; processResponding = $true; unexpectedWindowCount = 0; editSaveSucceeded = $true } })",
            "$coarseResult = Measure-C6SoakEvidence -Samples $coarse -DurationSeconds 7200 -SampleSeconds 600 -ExpectedWindowCount 20 -EvaluateGrowth $false",
            "@{",
            "  singleCount = $single.SampleCount",
            "  singleMinimum = $single.MinimumSampleCount",
            "  singleShortfall = $single.SampleCountShortfall",
            "  singleFailures = @($single.StructuralFailures)",
            "  fullCount = $full.SampleCount",
            "  fullExpected = $full.ExpectedSampleCount",
            "  fullMinimum = $full.MinimumSampleCount",
            "  fullFailures = @($full.StructuralFailures)",
            "  fullGrowthEvaluated = $full.GrowthEvaluated",
            "  fullGrowth = $full.WorkingSetGrowthPercent",
            "  gapMax = $gap.MaxGapSeconds",
            "  gapFailures = @($gap.StructuralFailures)",
            "  windowInstability = $window.WindowInstabilityCount",
            "  windowUnresponsive = $window.ProcessUnresponsiveCount",
            "  windowUnexpected = $window.UnexpectedWindowCount",
            "  windowFailures = @($window.StructuralFailures)",
            "  editFailures = $edit.EditSaveFailureCount",
            "  editFailureText = @($edit.StructuralFailures)",
            "  highGrowthPassed = $high.StructuralPassed",
            "  highGrowthPercent = $high.WorkingSetGrowthPercent",
            "  coarseCount = $coarseResult.SampleCount",
            "  coarseExpected = $coarseResult.ExpectedSampleCount",
            "  coarseMinimum = $coarseResult.MinimumSampleCount",
            "  coarseShortfall = $coarseResult.SampleCountShortfall",
            "  coarseFailures = @($coarseResult.StructuralFailures)",
            "} | ConvertTo-Json -Compress -Depth 6");

        var result = RunPowerShellCommand(command);
        Assert.Equal(0, result.ExitCode);

        var json = ParseLastJsonObject(result.StandardOutput);
        Assert.Equal(1, json.GetProperty("singleCount").GetInt32());
        Assert.Equal(120, json.GetProperty("singleMinimum").GetInt32());
        Assert.Equal(119, json.GetProperty("singleShortfall").GetInt32());
        Assert.NotEmpty(json.GetProperty("singleFailures").EnumerateArray());

        Assert.Equal(121, json.GetProperty("fullCount").GetInt32());
        Assert.Equal(121, json.GetProperty("fullExpected").GetInt32());
        Assert.Equal(120, json.GetProperty("fullMinimum").GetInt32());
        Assert.Empty(json.GetProperty("fullFailures").EnumerateArray());
        Assert.True(json.GetProperty("fullGrowthEvaluated").GetBoolean());
        var growth = json.GetProperty("fullGrowth").GetDouble();
        Assert.InRange(growth, 10.0, 13.0);

        Assert.True(json.GetProperty("gapMax").GetDouble() > 90);
        Assert.Contains(
            json.GetProperty("gapFailures").EnumerateArray().Select(item => item.GetString()),
            failure => failure is not null
                && failure.Contains("largest soak sample gap", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(1, json.GetProperty("windowInstability").GetInt32());
        Assert.Equal(1, json.GetProperty("windowUnresponsive").GetInt32());
        Assert.Equal(1, json.GetProperty("windowUnexpected").GetInt32());

        Assert.Equal(1, json.GetProperty("editFailures").GetInt32());
        Assert.Contains(
            json.GetProperty("editFailureText").EnumerateArray().Select(item => item.GetString()),
            failure => failure is not null
                && failure.Contains("edit/save", StringComparison.OrdinalIgnoreCase));

        Assert.True(json.GetProperty("highGrowthPassed").GetBoolean());
        Assert.True(json.GetProperty("highGrowthPercent").GetDouble() > 15);

        Assert.Equal(13, json.GetProperty("coarseCount").GetInt32());
        Assert.Equal(13, json.GetProperty("coarseExpected").GetInt32());
        Assert.Equal(120, json.GetProperty("coarseMinimum").GetInt32());
        Assert.Equal(107, json.GetProperty("coarseShortfall").GetInt32());
        Assert.Contains(
            json.GetProperty("coarseFailures").EnumerateArray().Select(item => item.GetString()),
            failure => failure is not null
                && failure.Contains("60 s sample cadence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TwoHourSoakCommand_UsesFreshRunIdAndEvidenceRoot()
    {
        var common = Path.Combine(ScriptsRoot(), "C6ScriptCommon.ps1");
        var scriptsRoot = ScriptsRoot();
        var originalRunId = "c6-task5-final-20261030-151739";
        var originalEvidence = Path.Combine(
            Path.GetTempPath(),
            "c6-task5-evidence",
            originalRunId);
        var command = string.Join(
            Environment.NewLine,
            "$ErrorActionPreference = 'Stop'",
            $". {PsQuote(common)}",
            "$result = New-C6TwoHourSoakCommand " +
            $"-ScriptRoot {PsQuote(scriptsRoot)} " +
            $"-RunId '{originalRunId}' " +
            "-Notes 1000 -Visible 20 -StartupIterations 10 -UiLaunches 10 " +
            "-ShowHideCycles 30 -SoakSampleSeconds 60 " +
            $"-EvidenceRoot {PsQuote(originalEvidence)} " +
            "-Timestamp '20261030-160000000'",
            "$result | ConvertTo-Json -Compress -Depth 5");

        var result = RunPowerShellCommand(command);
        Assert.Equal(0, result.ExitCode);

        var json = ParseLastJsonObject(result.StandardOutput);
        var freshRunId = json.GetProperty("runId").GetString();
        var freshEvidenceRoot = json.GetProperty("evidenceRoot").GetString();
        var generatedCommand = json.GetProperty("command").GetString();

        Assert.NotNull(freshRunId);
        Assert.NotNull(freshEvidenceRoot);
        Assert.NotNull(generatedCommand);
        Assert.StartsWith(originalRunId + "-2h-", freshRunId, StringComparison.Ordinal);
        Assert.NotEqual(originalRunId, freshRunId);
        Assert.NotEqual(originalEvidence, freshEvidenceRoot, StringComparer.OrdinalIgnoreCase);
        Assert.EndsWith(freshRunId, freshEvidenceRoot, StringComparison.Ordinal);
        Assert.Equal(60, json.GetProperty("sampleSeconds").GetInt32());
        Assert.Equal(121, json.GetProperty("requiredSampleCount").GetInt32());
        Assert.Equal(120, json.GetProperty("minimumSampleCount").GetInt32());
        Assert.Equal(90.0, json.GetProperty("cadenceLimitSeconds").GetDouble());
        Assert.False(json.GetProperty("growthThresholdsConfirmed").GetBoolean());
        Assert.Equal("diagnostic", json.GetProperty("growthThresholdRole").GetString());

        Assert.Contains("C6_BUDGETS_CONFIRMED='1'", generatedCommand, StringComparison.Ordinal);
        Assert.Contains(freshRunId, generatedCommand, StringComparison.Ordinal);
        Assert.Contains(freshEvidenceRoot, generatedCommand, StringComparison.Ordinal);
        Assert.Contains("-DurationMinutes 120", generatedCommand, StringComparison.Ordinal);
        Assert.Contains("-Notes 1000", generatedCommand, StringComparison.Ordinal);
        Assert.Contains("-Visible 20", generatedCommand, StringComparison.Ordinal);
        Assert.Contains("-StartupIterations 10", generatedCommand, StringComparison.Ordinal);
        Assert.Contains("-UiLaunches 10", generatedCommand, StringComparison.Ordinal);
        Assert.Contains("-ShowHideCycles 30", generatedCommand, StringComparison.Ordinal);
        Assert.Contains("-SoakSampleSeconds 60", generatedCommand, StringComparison.Ordinal);
        Assert.Contains(
            Path.Combine(scriptsRoot, "Invoke-C6Performance.ps1"),
            generatedCommand,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "-EvidenceRoot \"" + originalEvidence + "\"",
            generatedCommand,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TwoHourSoakCommand_RejectsCoarseSampleCadence()
    {
        var common = Path.Combine(ScriptsRoot(), "C6ScriptCommon.ps1");
        var originalEvidence = Path.Combine(
            Path.GetTempPath(),
            "c6-task5-evidence",
            "c6-task5-fixed-run");
        var command = string.Join(
            Environment.NewLine,
            "$ErrorActionPreference = 'Stop'",
            $". {PsQuote(common)}",
            "try {",
            "  $null = New-C6TwoHourSoakCommand " +
            $"-ScriptRoot {PsQuote(ScriptsRoot())} " +
            "-RunId 'c6-task5-fixed-run' -Notes 1000 -Visible 20 " +
            "-StartupIterations 10 -UiLaunches 10 -ShowHideCycles 30 " +
            "-SoakSampleSeconds 600 " +
            $"-EvidenceRoot {PsQuote(originalEvidence)}",
            "  'NO_THROW'",
            "}",
            "catch { $_.Exception.Message }");

        var result = RunPowerShellCommand(command);
        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("NO_THROW", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            "60 s sample cadence",
            result.StandardOutput,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConfirmedTinyShape_FailsClosedWithoutRunningTheProbe()
    {
        var runId = NewRunId();
        var evidenceRoot = Path.Combine(
            Path.GetTempPath(),
            "c6-perf-evidence-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = RunScriptAllowFailure(
                "Invoke-C6Performance.ps1",
                new Dictionary<string, string?>
                {
                    ["C6_BUDGETS_CONFIRMED"] = "1"
                },
                "-RunId", runId,
                "-Notes", "10",
                "-Visible", "2",
                "-StartupIterations", "1",
                "-UiLaunches", "1",
                "-ShowHideCycles", "0",
                "-SkipSoak",
                "-EvidenceRoot", evidenceRoot);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Equal("FAIL", result.Json.GetProperty("status").GetString());
            var caseResult = ReadCaseResult(runId, "C6-PERF");
            var reason = caseResult.GetProperty("reason").GetString();
            Assert.NotNull(reason);
            Assert.Contains("acceptance", reason, StringComparison.OrdinalIgnoreCase);

            var acceptance = caseResult.GetProperty("actual").GetProperty("acceptance");
            Assert.False(acceptance.GetProperty("passed").GetBoolean());
            Assert.True(acceptance.GetProperty("failures").GetArrayLength() >= 4);
            Assert.True(
                File.Exists(Path.Combine(evidenceRoot, "results", "C6-PERF.result.json")),
                "The fail-closed acceptance result was not archived.");
        }
        finally
        {
            TryDeleteRunRoot(runId);
            TryDeleteDirectory(evidenceRoot);
        }
    }
}
