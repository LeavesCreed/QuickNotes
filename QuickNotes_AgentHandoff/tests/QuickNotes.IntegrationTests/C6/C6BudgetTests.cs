using QuickNotes.C6.Harness;

namespace QuickNotes.IntegrationTests.C6;

public sealed class C6BudgetTests
{
    [Fact]
    public void Budget_CandidateValuesArePositiveAndDistinct()
    {
        Assert.Equal(1000, C6Budget.HistoricalNotes);
        Assert.Equal(20, C6Budget.VisibleWindows);
        Assert.True(C6Budget.WorkingSetBytes > 0);

        var durations = new[]
        {
            C6Budget.StartupReadyP95,
            C6Budget.StartupWithUiP95,
            C6Budget.SaveP95,
            C6Budget.SaveP99,
            C6Budget.InputP95
        };
        Assert.All(durations, duration => Assert.True(duration > TimeSpan.Zero));
        Assert.Equal(durations.Length, durations.Distinct().Count());
    }

    [Fact]
    public void Budget_IsExplicitlyUnconfirmedUntilTheUserApprovesIt()
    {
        Assert.Equal("candidate-unconfirmed", C6Budget.ApprovalStatus);
        Assert.True(C6Budget.RequiresUserConfirmation);
    }
}
