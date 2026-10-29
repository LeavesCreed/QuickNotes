namespace QuickNotes.Application;

public sealed record BackupRuntimeStatus
{
    public const int CurrentFormatVersion = 1;

    public required int FormatVersion { get; init; }
    public string? BackupDirectory { get; init; }
    public DateTimeOffset? LastCheckAtUtc { get; init; }
    public DateTimeOffset? LastSuccessAtUtc { get; init; }
    public DateTimeOffset? LastFailureAtUtc { get; init; }
    public string? LastErrorCode { get; init; }
    public string? LastWarning { get; init; }
    public string? LastSuccessfulPackagePath { get; init; }
    public bool AutoBackupConfigured { get; init; }

    public static BackupRuntimeStatus NotConfigured(DateTimeOffset now) => new()
    {
        FormatVersion = CurrentFormatVersion,
        LastCheckAtUtc = now,
        AutoBackupConfigured = false
    };
}
