namespace QuickNotes.Desktop.Backup;

public sealed record BackupHostOptions
{
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromMinutes(15);

    public void Validate()
    {
        if (CheckInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(CheckInterval),
                CheckInterval,
                "Backup check interval must be positive.");
        }
    }
}
