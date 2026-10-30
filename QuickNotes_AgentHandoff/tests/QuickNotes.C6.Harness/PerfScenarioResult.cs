namespace QuickNotes.C6.Harness;

/// <summary>
/// Task 5 service-level performance result. It contains timings, counts,
/// process resources, and budget verdicts only; note text and machine paths are
/// never emitted. <see cref="Passed"/> means every measured real service
/// operation completed as expected, not that a performance budget passed;
/// <see cref="PerformanceStatus"/> carries the explicit budget verdict
/// (<c>pass</c>, <c>fail</c>, or <c>unconfirmed</c>).
/// </summary>
public sealed record PerfScenarioResult
{
    public string Scenario { get; init; } = "perf";
    public required string Status { get; init; }
    public required bool Passed { get; init; }
    public required string BudgetStatus { get; init; }
    public required string PerformanceStatus { get; init; }
    public required int NoteCount { get; init; }
    public required int VisibleCount { get; init; }
    public required int Iterations { get; init; }
    public required IReadOnlyList<MetricSample> Metrics { get; init; }
    public required IReadOnlyList<BudgetCheck> BudgetChecks { get; init; }
    public required PerfBudgetEvidence Budget { get; init; }
    public required IReadOnlyList<PerfRestoreCheck> RestoreChecks { get; init; }
    public string? FailureStage { get; init; }
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }
}

/// <summary>
/// Budget constants echoed by the harness so the PowerShell driver does not
/// duplicate them. The values are only acceptance limits when
/// <see cref="BudgetStatus"/> is <c>confirmed</c>.
/// </summary>
public sealed record PerfBudgetEvidence
{
    public required string ConfirmationEnvironmentVariable { get; init; }
    public required string ApprovalStatus { get; init; }
    public required double StartupReadyP95Ms { get; init; }
    public required double StartupWithUiP95Ms { get; init; }
    public required double SaveP95Ms { get; init; }
    public required double SaveP99Ms { get; init; }
    public required double InputP95Ms { get; init; }
    public required long WorkingSetBytes { get; init; }
    public required int HistoricalNotes { get; init; }
    public required int VisibleWindows { get; init; }
}

public sealed record PerfRestoreCheck
{
    public required int NoteCount { get; init; }
    public required string BackupStatus { get; init; }
    public required string RestoreStatus { get; init; }
    public required int RestoredNoteCount { get; init; }
    public required double DurationMs { get; init; }
    public required bool Passed { get; init; }
    public string? Message { get; init; }
}
