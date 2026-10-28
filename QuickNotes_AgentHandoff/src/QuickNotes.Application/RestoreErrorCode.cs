namespace QuickNotes.Application;

public enum RestoreErrorCode
{
    None = 0,
    ValidationFailed,
    PackageNotFound,
    PackageInvalid,
    SchemaIncompatible,
    CurrentDatabaseUnavailable,
    DestinationUnavailable,
    StagingUnavailable,
    InsufficientSpace,
    PendingDrafts,
    DraftCheckFailed,
    ProtectionSnapshotFailed,
    SwitchFailed,
    PostRestoreVerificationFailed,
    RollbackFailed,
    WritesBlocked
}
