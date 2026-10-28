namespace QuickNotes.Desktop.Startup;

public enum StartupArtifactKind
{
    RestoreDirectory,
    RecoveryDirectory,
    BackupTempDirectory,
    PackageTempFile,
    VerifyTempFile,
    Sidecar,
    ProtectionSnapshot,
    QuarantinedDatabase,
    DraftFile
}

public sealed record StartupArtifact(
    string Path,
    StartupArtifactKind Kind,
    bool IsDirectory,
    bool MayContainRecoveryData);

public sealed record StartupArtifactInventory(IReadOnlyList<StartupArtifact> Artifacts)
{
    public bool HasDataEvidence => Artifacts.Count > 0;
}
