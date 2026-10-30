using System.Text.Json;
using QuickNotes.C6.Harness;

namespace QuickNotes.IntegrationTests.C6;

/// <summary>
/// Task 5 performance-collection contracts. The default suite only exercises
/// the in-process metrics/percentile/gate contracts and one small real
/// service-perf run against synthetic <c>%TEMP%\QuickNotes-C6</c> data. Real
/// WPF launches and the multi-hour soak are opt-in script evidence and never
/// run in the default suite.
/// </summary>
public sealed class C6PerformanceTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    [Fact]
    public void MetricsCollector_ReportsPercentilesAndNeverSerializesNoteText()
    {
        var samples = new[] { 10d, 20d, 30d, 40d, 100d };
        var metric = MetricsCollector.FromMilliseconds("save", samples);

        Assert.Equal(30, metric.P50Ms, 0.1);
        Assert.Equal(100, metric.P95Ms, 0.1);
        Assert.Equal(100, metric.P99Ms, 0.1);
        Assert.Equal(100, metric.MaxMs, 0.1);
        Assert.Equal(5, metric.SampleCount);
        Assert.Null(metric.Error);
        Assert.DoesNotContain("body", JsonSerializer.Serialize(metric), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MetricsCollector_EmptyInputIsAnExplicitNoSamplesError()
    {
        var metric = MetricsCollector.FromMilliseconds("save", Array.Empty<double>());

        Assert.Equal("save", metric.Name);
        Assert.Equal("no_samples", metric.Error);
        Assert.Equal(0, metric.SampleCount);
        Assert.False(metric.HasSamples);
        Assert.Equal(MetricsCollector.ErrorDurationMs, metric.P50Ms);
        Assert.Equal(MetricsCollector.ErrorDurationMs, metric.P95Ms);
        Assert.Equal(MetricsCollector.ErrorDurationMs, metric.P99Ms);
        Assert.Equal(MetricsCollector.ErrorDurationMs, metric.MaxMs);
        Assert.DoesNotContain("body", JsonSerializer.Serialize(metric), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MetricsCollector_ErrorAggregatesNeverLookLikeFastZeroSamples()
    {
        var partial = MetricsCollector.Aggregate(
            "save",
            new[]
            {
                MetricsCollector.FromMilliseconds("save", new[] { 1d }),
                new MetricSample { Name = "save", Error = MetricsCollector.NoSamplesError }
            });

        Assert.Equal(MetricsCollector.PartialSamplesError, partial.Error);
        Assert.Equal(1, partial.SampleCount);
        Assert.False(partial.HasSamples);

        // A consumer that ignores Error must still see values far above every
        // budget instead of a zero-millisecond pass.
        Assert.Equal(MetricsCollector.ErrorDurationMs, partial.P50Ms);
        Assert.True(partial.P95Ms > C6Budget.SaveP95.TotalMilliseconds);
        Assert.True(partial.P99Ms > C6Budget.SaveP99.TotalMilliseconds);
        Assert.True(partial.MaxMs > C6Budget.SaveP95.TotalMilliseconds);
    }

    [Theory]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void MetricsCollector_RejectsInvalidDurations(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MetricsCollector.FromMilliseconds("save", new[] { value }));
    }

    [Fact]
    public async Task MetricsCollector_SampleAsyncCapturesDurationAndProcessMetrics()
    {
        var metric = await MetricsCollector.SampleAsync(
            "startup_host",
            async cancellationToken => await Task.Delay(5, cancellationToken));

        Assert.Equal("startup_host", metric.Name);
        Assert.Equal(1, metric.SampleCount);
        Assert.Null(metric.Error);
        Assert.True(metric.P50Ms >= 1, $"p50 {metric.P50Ms} was below a measurable delay.");
        Assert.Equal(metric.P50Ms, metric.P95Ms, 0.001);
        Assert.Equal(metric.P50Ms, metric.P99Ms, 0.001);
        Assert.True(metric.WorkingSetBytes > 0);
        Assert.True(metric.HandleCount > 0);
        Assert.True(metric.AllocatedBytes >= 0);
    }

    [Fact]
    public void MetricsCollector_AggregateKeepsNoSamplesHonest()
    {
        var empty = MetricsCollector.Aggregate(
            "save",
            Array.Empty<MetricSample>());
        Assert.Equal("no_samples", empty.Error);
        Assert.False(empty.HasSamples);
        Assert.Equal(MetricsCollector.ErrorDurationMs, empty.P50Ms);

        var samples = new[]
        {
            MetricsCollector.FromMilliseconds("save", new[] { 1d }),
            MetricsCollector.FromMilliseconds("save", new[] { 2d }),
            MetricsCollector.FromMilliseconds("save", new[] { 3d })
        };
        var aggregate = MetricsCollector.Aggregate("save", samples);
        Assert.Equal(3, aggregate.SampleCount);
        Assert.Equal(2, aggregate.P50Ms, 0.1);
        Assert.Equal(3, aggregate.MaxMs, 0.1);
        Assert.Null(aggregate.Error);
    }

    [Fact]
    public void PerformanceGate_UnconfirmedNeverClaimsPass()
    {
        var verdict = C6PerformanceGate.Evaluate(
            confirmed: false,
            new[]
            {
                new BudgetCheckInput(
                    "save_p95",
                    10_000,
                    C6Budget.SaveP95.TotalMilliseconds,
                    "ms")
            });

        Assert.Equal("unconfirmed", verdict.BudgetStatus);
        Assert.Equal("unconfirmed", verdict.PerformanceStatus);
        Assert.All(verdict.Checks, check => Assert.Equal("unconfirmed", check.Status));
        Assert.All(verdict.Checks, check => Assert.False(check.Passed));
    }

    [Fact]
    public void PerformanceGate_ConfirmedOverBudgetFailsEvenWhenTheScenarioCompleted()
    {
        var verdict = C6PerformanceGate.Evaluate(
            confirmed: true,
            new[]
            {
                new BudgetCheckInput(
                    "save_p95",
                    C6Budget.SaveP95.TotalMilliseconds + 1,
                    C6Budget.SaveP95.TotalMilliseconds,
                    "ms")
            });

        Assert.Equal("confirmed", verdict.BudgetStatus);
        Assert.Equal("fail", verdict.PerformanceStatus);
        Assert.Equal("fail", Assert.Single(verdict.Checks).Status);
    }

    [Fact]
    public void PerformanceGate_ConfirmedWithinBudgetPasses()
    {
        var verdict = C6PerformanceGate.Evaluate(
            confirmed: true,
            new[]
            {
                new BudgetCheckInput(
                    "startup_host_p95",
                    C6Budget.StartupReadyP95.TotalMilliseconds,
                    C6Budget.StartupReadyP95.TotalMilliseconds,
                    "ms"),
                new BudgetCheckInput(
                    "memory",
                    C6Budget.WorkingSetBytes,
                    C6Budget.WorkingSetBytes,
                    "bytes")
            });

        Assert.Equal("confirmed", verdict.BudgetStatus);
        Assert.Equal("pass", verdict.PerformanceStatus);
        Assert.All(verdict.Checks, check => Assert.True(check.Passed));
    }

    [Fact]
    public void PerformanceGate_EmptyCheckListCannotPass()
    {
        var verdict = C6PerformanceGate.Evaluate(true, Array.Empty<BudgetCheckInput>());

        Assert.Equal("confirmed", verdict.BudgetStatus);
        Assert.Equal("fail", verdict.PerformanceStatus);
        Assert.Contains(verdict.Checks, check => check.Name == "checks");
    }

    [Fact]
    public async Task PerfScenario_SmallProbeCompletesAllMeasurementsAndStaysUnconfirmedWithoutNoteText()
    {
        using var temp = new C6TemporaryDirectory();
        var runId = Path.GetFileName(temp.RootPath);
        var dataRoot = Path.Combine(temp.RootPath, "data");
        var outputPath = Path.Combine(temp.RootPath, "perf.json");
        var options = HarnessOptions.Parse(new[]
        {
            "perf",
            "--data-root", dataRoot,
            "--notes", "12",
            "--visible", "3",
            "--iterations", "2",
            "--output", outputPath,
            "--run-id", runId
        });

        var result = await PerfScenario
            .RunAsync(options, budgetConfirmed: false, CancellationToken.None)
            .WaitAsync(TimeSpan.FromMinutes(2));

        Assert.Equal("perf", result.Scenario);
        Assert.Equal("ok", result.Status);
        Assert.True(result.Passed);
        Assert.Equal("unconfirmed", result.BudgetStatus);
        Assert.Equal("unconfirmed", result.PerformanceStatus);
        Assert.All(result.BudgetChecks, check =>
        {
            Assert.Equal("unconfirmed", check.Status);
            Assert.False(check.Passed);
        });
        Assert.Equal(12, result.NoteCount);
        Assert.Contains(result.Metrics, metric =>
            metric.Name == "startup_host" && metric.SampleCount == 2);
        Assert.Contains(result.Metrics, metric =>
            metric.Name == "save" && metric.SampleCount == 50);
        Assert.Contains(result.Metrics, metric =>
            metric.Name == "input" && metric.SampleCount == 100);
        Assert.Contains(result.Metrics, metric =>
            metric.Name == "backup_12_complete" && metric.SampleCount == 1);
        Assert.Contains(result.Metrics, metric =>
            metric.Name == "restore_12_complete" && metric.SampleCount == 1);
        Assert.All(result.RestoreChecks, check => Assert.True(
            check.Passed,
            $"Restore size {check.NoteCount} failed: {check.Message}"));
        Assert.True(File.Exists(outputPath));
        AssertNoSyntheticNoteText(File.ReadAllText(outputPath));

        var json = JsonSerializer.Serialize(result, SerializerOptions);
        Assert.DoesNotContain("\"body\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"title\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(C6DataFactory.SyntheticBodyMarker, json, StringComparison.Ordinal);
    }

    [Fact]
    public void PerfScenario_EnvironmentGateRequiresExactOne()
    {
        Assert.False(C6Budget.IsConfirmedInEnvironmentValue(null));
        Assert.False(C6Budget.IsConfirmedInEnvironmentValue(string.Empty));
        Assert.False(C6Budget.IsConfirmedInEnvironmentValue("true"));
        Assert.False(C6Budget.IsConfirmedInEnvironmentValue("01"));
        Assert.True(C6Budget.IsConfirmedInEnvironmentValue("1"));
    }

    [Fact]
    public async Task PerfScenario_ProcessHarnessRunsTheRealBodyAndWritesAnOutputMarker()
    {
        using var temp = new C6TemporaryDirectory();
        var runId = Path.GetFileName(temp.RootPath);
        var dataRoot = Path.Combine(temp.RootPath, "data");
        var outputPath = Path.Combine(temp.RootPath, "perf.json");
        var options = HarnessOptions.Parse(new[]
        {
            "perf",
            "--data-root", dataRoot,
            "--notes", "5",
            "--visible", "1",
            "--iterations", "1",
            "--output", outputPath,
            "--run-id", runId
        });
        var harnessAssembly = typeof(PerfScenario).Assembly.Location;
        using var process = await ProcessHarness.StartAsync(
            harnessAssembly,
            options,
            new Dictionary<string, string?>
            {
                [C6Budget.ConfirmationEnvironmentVariable] = "0"
            });
        try
        {
            await ProcessHarness.WaitForMarkerAsync(outputPath, TimeSpan.FromMinutes(1));
            await ProcessHarness.WaitForExitAsync(process, TimeSpan.FromSeconds(30));
            Assert.Equal(0, process.ExitCode);

            using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
            var root = document.RootElement;
            Assert.Equal("perf", root.GetProperty("scenario").GetString());
            Assert.Equal("ok", root.GetProperty("status").GetString());
            Assert.Equal("unconfirmed", root.GetProperty("budgetStatus").GetString());
            Assert.Equal("unconfirmed", root.GetProperty("performanceStatus").GetString());
            AssertNoSyntheticNoteText(File.ReadAllText(outputPath));
        }
        finally
        {
            await ProcessHarness.KillTreeAsync(process);
        }
    }

    private static void AssertNoSyntheticNoteText(string text)
    {
        Assert.DoesNotContain("\"body\"", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"title\"", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(C6DataFactory.SyntheticBodyMarker, text, StringComparison.Ordinal);
    }
}
