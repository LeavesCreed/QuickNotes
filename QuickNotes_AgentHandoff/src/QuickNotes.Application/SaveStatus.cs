namespace QuickNotes.Application;

public enum SaveStatus
{
    Saved,
    Conflict,
    NotFound,
    ValidationFailed,
    StorageUnavailable,
    OutcomeUnknown
}
