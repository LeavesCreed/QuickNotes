namespace QuickNotes.Application;

public enum BackupErrorCode
{
    None = 0,
    ValidationFailed,
    SnapshotFailed,
    SnapshotVerificationFailed,
    PackageWriteFailed,
    PackageVerificationFailed,
    DestinationUnavailable,
    SourceUnavailable
}
