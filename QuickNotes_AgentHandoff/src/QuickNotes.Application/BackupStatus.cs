namespace QuickNotes.Application;

public enum BackupStatus
{
    Succeeded,
    ValidationFailed,
    SnapshotFailed,
    VerificationFailed,
    StorageUnavailable
}
