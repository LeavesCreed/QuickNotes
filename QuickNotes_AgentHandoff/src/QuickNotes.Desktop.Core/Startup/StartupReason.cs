namespace QuickNotes.Desktop.Startup;

public enum StartupReason
{
    None,
    Initialized,
    DatabaseMissing,
    DatabaseCorrupt,
    SchemaUnsupported,
    StateMissingWithDatabase,
    StateInvalid,
    InitializationIncomplete,
    WalSidecarPresent,
    WritesBlocked,
    RestoreJournalIncomplete,
    RestoreJournalInvalid,
    UnverifiedArtifacts
}
