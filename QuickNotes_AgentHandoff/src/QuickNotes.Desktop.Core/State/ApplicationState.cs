namespace QuickNotes.Desktop.State;

public sealed record ApplicationState
{
    public const int CurrentFormatVersion = 1;

    public required int FormatVersion { get; init; }
    public required Guid InstallationId { get; init; }
    public required ApplicationInitializationStatus InitializationStatus { get; init; }
    public required int SchemaVersion { get; init; }
    public required DateTimeOffset LastStartupUtc { get; init; }
    public bool WritesBlocked { get; init; }
    public string? WriteBlockReason { get; init; }
    public DateTimeOffset? WriteBlockedAtUtc { get; init; }
    public string? RecoverySourceSha256 { get; init; }
    public bool? RecoveryProtectionSnapshotCreated { get; init; }
    public DateTimeOffset? LastRecoveryAtUtc { get; init; }

    public static ApplicationState CreateInitializing(
        Guid installationId,
        DateTimeOffset now) => new()
        {
            FormatVersion = CurrentFormatVersion,
            InstallationId = installationId,
            InitializationStatus = ApplicationInitializationStatus.Initializing,
            SchemaVersion = 0,
            LastStartupUtc = now
        };

    public ApplicationState MarkReady(int schemaVersion, DateTimeOffset now) => this with
    {
        InitializationStatus = ApplicationInitializationStatus.Ready,
        SchemaVersion = schemaVersion,
        LastStartupUtc = now
    };

    public ApplicationState MarkWritesBlocked(string reason, DateTimeOffset now) => this with
    {
        WritesBlocked = true,
        WriteBlockReason = reason,
        WriteBlockedAtUtc = now
    };
}
