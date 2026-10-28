using QuickNotes.Application;
using QuickNotes.Desktop.Paths;
using QuickNotes.Desktop.State;

namespace QuickNotes.Desktop.Startup;

public sealed record StartupAssessment
{
    public required StartupMode Mode { get; init; }
    public required StartupReason Reason { get; init; }
    public required string Message { get; init; }
    public string? ErrorCode { get; init; }
    public ApplicationState? State { get; init; }
    public RestoreJournal? RestoreJournal { get; init; }
    public RestoreDatabaseInfo? DatabaseInfo { get; init; }
    public StartupArtifactInventory Artifacts { get; init; } = new(Array.Empty<StartupArtifact>());

    public bool CanEnterNormalUse => Mode is StartupMode.FirstRunInitialized or StartupMode.Ready;
}
