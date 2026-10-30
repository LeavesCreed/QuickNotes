namespace QuickNotes.C6.Harness;

/// <summary>
/// C0 candidate performance budgets. These values are provisional inputs for
/// Task 5; they are not user-approved acceptance criteria. Until the user
/// confirms them (or explicitly accepts a baseline-only run), the C6 harness
/// must report <see cref="ApprovalStatus"/> and must not claim a performance pass.
/// Task 5 must gate comparisons on <see cref="ConfirmationEnvironmentVariable"/>
/// being set to "1" by the user; Task 1 never sets or reads it.
/// </summary>
public static class C6Budget
{
    /// <summary>
    /// Environment variable Task 5's perf gate must check. Any other value
    /// (including unset) means baseline-only: emit raw metrics with
    /// budgetStatus=unconfirmed and never claim a pass.
    /// </summary>
    public const string ConfirmationEnvironmentVariable = "C6_BUDGETS_CONFIRMED";

    public const string ApprovalStatus = "candidate-unconfirmed";
    public const bool RequiresUserConfirmation = true;

    public const int HistoricalNotes = 1000;
    public const int VisibleWindows = 20;
    public static readonly TimeSpan StartupReadyP95 = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan StartupWithUiP95 = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan SaveP95 = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan SaveP99 = TimeSpan.FromMilliseconds(1500);
    public static readonly TimeSpan InputP95 = TimeSpan.FromMilliseconds(100);
    public const long WorkingSetBytes = 300L * 1024 * 1024;
}
