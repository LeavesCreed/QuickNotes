namespace StickyNotes.Application;

public enum SaveErrorCode
{
    None = 0,
    NoteNotFound,
    ConcurrencyConflict,
    ValidationFailed,
    StorageBusy,
    StorageLocked,
    StorageUnavailable,
    CommitFailedNotSaved,
    CommitOutcomeUnknown,
    DraftCleanupFailed,
    DraftMismatch
}
