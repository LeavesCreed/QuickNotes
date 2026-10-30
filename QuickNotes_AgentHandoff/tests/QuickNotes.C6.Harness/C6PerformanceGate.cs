namespace QuickNotes.C6.Harness;

/// <summary>
/// One budget comparison input. <see cref="Unit"/> is only informational
/// (<c>ms</c>, <c>bytes</c>, <c>count</c>) so reports stay readable.
/// </summary>
public sealed record BudgetCheckInput(
    string Name,
    double Actual,
    double Limit,
    string Unit);

public sealed record BudgetCheck
{
    public required string Name { get; init; }
    public required double Actual { get; init; }
    public required double Limit { get; init; }
    public required string Unit { get; init; }
    public required string Status { get; init; }
    public required bool Passed { get; init; }
}

/// <summary>
/// Combined result of the C6 Task 5 budget gate. <see cref="PerformanceStatus"/>
/// is <c>unconfirmed</c> unless the user's environment explicitly set
/// <c>C6_BUDGETS_CONFIRMED=1</c>; an unconfirmed run never reports pass even
/// when every raw measurement happens to be under the candidate limits.
/// </summary>
public sealed record PerformanceVerdict
{
    public required string BudgetStatus { get; init; }
    public required string PerformanceStatus { get; init; }
    public required IReadOnlyList<BudgetCheck> Checks { get; init; }
}

public static class C6PerformanceGate
{
    public const string Pass = "pass";
    public const string Fail = "fail";
    public const string Unconfirmed = "unconfirmed";

    public static PerformanceVerdict Evaluate(
        bool confirmed,
        IReadOnlyList<BudgetCheckInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var budgetStatus = confirmed
            ? C6Budget.ConfirmedBudgetStatus
            : C6Budget.UnconfirmedBudgetStatus;
        if (inputs.Count == 0)
        {
            return new PerformanceVerdict
            {
                BudgetStatus = budgetStatus,
                PerformanceStatus = confirmed ? Fail : Unconfirmed,
                Checks = new[]
                {
                    new BudgetCheck
                    {
                        Name = "checks",
                        Actual = 0,
                        Limit = 1,
                        Unit = "count",
                        Status = confirmed ? Fail : Unconfirmed,
                        Passed = false
                    }
                }
            };
        }

        var checks = new List<BudgetCheck>(inputs.Count);
        var allPassed = true;
        foreach (var input in inputs)
        {
            if (input is null)
            {
                throw new ArgumentException("Budget inputs must not contain null.", nameof(inputs));
            }

            var passed = confirmed && input.Actual <= input.Limit;
            var status = !confirmed
                ? Unconfirmed
                : input.Actual <= input.Limit ? Pass : Fail;
            allPassed &= passed;
            checks.Add(new BudgetCheck
            {
                Name = input.Name,
                Actual = input.Actual,
                Limit = input.Limit,
                Unit = input.Unit,
                Status = status,
                Passed = passed
            });
        }

        return new PerformanceVerdict
        {
            BudgetStatus = budgetStatus,
            PerformanceStatus = !confirmed ? Unconfirmed : allPassed ? Pass : Fail,
            Checks = checks
        };
    }
}
