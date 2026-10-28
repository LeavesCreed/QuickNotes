namespace QuickNotes.Desktop.Editing;

public enum EditorSaveResolutionStatus
{
    Saved,
    Conflict,
    SaveFailed,
    OutcomeUnknown
}

public sealed record EditorSaveResolution(
    EditorSaveResolutionStatus Status,
    string? ErrorCode,
    string? Message);
