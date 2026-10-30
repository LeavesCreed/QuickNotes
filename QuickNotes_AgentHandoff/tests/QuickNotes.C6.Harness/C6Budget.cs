namespace QuickNotes.C6.Harness;

/// <summary>
/// C0 candidate performance budgets. These values are provisional inputs for
/// Task 5; they are not user-approved acceptance criteria. Until the user
/// confirms them (or explicitly accepts a baseline-only run), the C6 harness
/// must report <see cref="ApprovalStatus"/> and must not claim a performance pass.
/// </summary>
public static class C6Budget
{
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
