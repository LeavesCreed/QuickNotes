namespace StickyNotes.Application;

public enum SaveStatus
{
    Saved,
    Conflict,
    NotFound,
    ValidationFailed,
    StorageUnavailable,
    OutcomeUnknown
}
