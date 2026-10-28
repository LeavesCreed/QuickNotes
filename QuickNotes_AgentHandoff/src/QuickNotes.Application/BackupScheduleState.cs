namespace QuickNotes.Application;

public sealed record BackupScheduleState
{
    public const int CurrentFormatVersion = 1;

    public required int FormatVersion { get; init; }
    public required string LastSuccessfulFingerprint { get; init; }
    public required DateTimeOffset LastSuccessfulDailyAtUtc { get; init; }
    public required DateOnly LastSuccessfulDailyLocalDate { get; init; }
}
