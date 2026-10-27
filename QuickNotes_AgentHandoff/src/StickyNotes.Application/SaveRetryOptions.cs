namespace StickyNotes.Application;

public sealed record SaveRetryOptions
{
    public int MaxAttempts { get; init; } = 3;

    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } =
        new[] { TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(150) };
}
