using System.Diagnostics;

namespace QuickNotes.C6.Harness;

/// <summary>
/// One performance measurement. Only names, counts, timings, and process
/// resource values are ever emitted; note text is never read by this type.
/// <see cref="Error"/> is set to <c>no_samples</c> (or <c>partial_samples</c>)
/// instead of inventing a zero millisecond result, so an empty measurement can
/// never look like a fast pass.
/// </summary>
public sealed record MetricSample
{
    public required string Name { get; init; }
    public int SampleCount { get; init; }
    public double P50Ms { get; init; }
    public double P95Ms { get; init; }
    public double P99Ms { get; init; }
    public double MaxMs { get; init; }

    /// <summary>
    /// Total allocated bytes observed across the sampled operations. For a
    /// single <see cref="MetricsCollector.SampleAsync"/> call this is the
    /// allocation delta of that one operation.
    /// </summary>
    public long AllocatedBytes { get; init; }

    /// <summary>Peak working set observed while sampling.</summary>
    public long WorkingSetBytes { get; init; }

    /// <summary>Peak handle count observed while sampling.</summary>
    public int HandleCount { get; init; }

    public string? Error { get; init; }

    public bool HasSamples => Error is null && SampleCount > 0;
}

/// <summary>
/// Test-only metrics collector for C6 Task 5. Percentiles use the nearest-rank
/// method on the sorted samples, which is deterministic for the small sample
/// counts used by the perf probes (for example p50 of five values is the
/// third value). The collector never captures note content.
/// </summary>
public static class MetricsCollector
{
    public const string NoSamplesError = "no_samples";
    public const string PartialSamplesError = "partial_samples";

    /// <summary>
    /// Duration sentinel written into error samples. It is deliberately larger
    /// than every C6 budget so a consumer that ignores <see cref="MetricSample.Error"/>
    /// cannot mistake an aggregate of failed samples for a fast (zero-ms) pass.
    /// </summary>
    public const double ErrorDurationMs = double.MaxValue;

    /// <summary>
    /// Runs <paramref name="action"/> once and returns a single-sample metric
    /// with the elapsed milliseconds and the process allocation, working set,
    /// and handle snapshots taken around it. Exceptions propagate; a failed
    /// operation never becomes a zero-duration sample.
    /// </summary>
    public static async Task<MetricSample> SampleAsync(
        string name,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        ArgumentNullException.ThrowIfNull(action);

        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
        var startedAt = Stopwatch.GetTimestamp();
        await action(cancellationToken).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        var allocatedAfter = GC.GetTotalAllocatedBytes(precise: false);
        var process = Process.GetCurrentProcess();
        try
        {
            process.Refresh();
            return new MetricSample
            {
                Name = name,
                SampleCount = 1,
                P50Ms = elapsed,
                P95Ms = elapsed,
                P99Ms = elapsed,
                MaxMs = elapsed,
                AllocatedBytes = Math.Max(0, allocatedAfter - allocatedBefore),
                WorkingSetBytes = process.WorkingSet64,
                HandleCount = process.HandleCount
            };
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>
    /// Builds a percentile summary from already measured millisecond values.
    /// Empty input is an explicit <see cref="NoSamplesError"/> result, not a
    /// zero-threshold pass.
    /// </summary>
    public static MetricSample FromMilliseconds(
        string name,
        IReadOnlyList<double> values) =>
        FromMilliseconds(name, values, allocatedBytes: 0, workingSetBytes: 0, handleCount: 0);

    public static MetricSample FromMilliseconds(
        string name,
        IReadOnlyList<double> values,
        long allocatedBytes,
        long workingSetBytes,
        int handleCount)
    {
        ValidateName(name);
        ArgumentNullException.ThrowIfNull(values);
        if (allocatedBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(allocatedBytes));
        }

        if (workingSetBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workingSetBytes));
        }

        if (handleCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(handleCount));
        }

        if (values.Count == 0)
        {
            return ErrorSample(name, NoSamplesError);
        }

        var ordered = new double[values.Count];
        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index];
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(values),
                    value,
                    "Metric durations must be finite and non-negative.");
            }

            ordered[index] = value;
        }

        Array.Sort(ordered);
        return new MetricSample
        {
            Name = name,
            SampleCount = ordered.Length,
            P50Ms = NearestRank(ordered, 50),
            P95Ms = NearestRank(ordered, 95),
            P99Ms = NearestRank(ordered, 99),
            MaxMs = ordered[^1],
            AllocatedBytes = allocatedBytes,
            WorkingSetBytes = workingSetBytes,
            HandleCount = handleCount
        };
    }

    /// <summary>
    /// Combines single-operation samples into one metric. If any input sample
    /// is itself a failure the aggregate reports <c>partial_samples</c> (or
    /// <c>no_samples</c>) instead of silently averaging the successful subset.
    /// </summary>
    public static MetricSample Aggregate(
        string name,
        IReadOnlyList<MetricSample> samples)
    {
        ValidateName(name);
        ArgumentNullException.ThrowIfNull(samples);
        var valid = new List<MetricSample>(samples.Count);
        foreach (var sample in samples)
        {
            if (sample is null)
            {
                throw new ArgumentException("Samples must not contain null.", nameof(samples));
            }

            if (sample.HasSamples)
            {
                valid.Add(sample);
            }
        }

        if (valid.Count == 0)
        {
            return ErrorSample(name, NoSamplesError);
        }

        if (valid.Count != samples.Count)
        {
            // Report the number of successful samples for diagnostics, but keep
            // every percentile at the non-zero sentinel so this aggregate can
            // never be read as a fast result.
            return ErrorSample(name, PartialSamplesError, valid.Count);
        }

        var durations = new double[valid.Count];
        long allocated = 0;
        long workingSet = 0;
        var handles = 0;
        for (var index = 0; index < valid.Count; index++)
        {
            // Aggregate expects single-operation samples, whose P50Ms is the
            // one measured duration; FromMilliseconds summaries are not
            // meant to be re-aggregated.
            durations[index] = valid[index].P50Ms;
            allocated += valid[index].AllocatedBytes;
            workingSet = Math.Max(workingSet, valid[index].WorkingSetBytes);
            handles = Math.Max(handles, valid[index].HandleCount);
        }

        return FromMilliseconds(name, durations, allocated, workingSet, handles);
    }

    private static MetricSample ErrorSample(string name, string error, int sampleCount = 0) =>
        new()
        {
            Name = name,
            SampleCount = sampleCount,
            P50Ms = ErrorDurationMs,
            P95Ms = ErrorDurationMs,
            P99Ms = ErrorDurationMs,
            MaxMs = ErrorDurationMs,
            Error = error
        };

    private static double NearestRank(IReadOnlyList<double> ordered, int percentile)
    {
        var rank = (int)Math.Ceiling(percentile / 100.0 * ordered.Count);
        rank = Math.Clamp(rank, 1, ordered.Count);
        return ordered[rank - 1];
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Metric name must not be empty.", nameof(name));
        }
    }
}
