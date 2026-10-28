namespace QuickNotes.Application;

public static class BackupErrorCodes
{
    public const string SourceUnavailable = "source_unavailable";
    public const string SnapshotFailed = "snapshot_failed";
    public const string SnapshotVerificationFailed = "snapshot_verification_failed";
    public const string PackageWriteFailed = "package_write_failed";
    public const string PackageVerificationFailed = "package_verification_failed";
    public const string DestinationUnavailable = "destination_unavailable";
    public const string ManifestInvalid = "manifest_invalid";
}
