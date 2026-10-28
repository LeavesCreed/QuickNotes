namespace QuickNotes.Desktop.Startup;

public enum StartupReason
{
    None,
    Initialized,
    DatabaseMissing,
    DatabaseCorrupt,
    SchemaUnsupported,
    SchemaMigrationFailed,
    StateMissingWithDatabase,
    StateInvalid,
    InitializationIncomplete,
    WalSidecarPresent,
    WritesBlocked,
    RestoreJournalIncomplete,
    RestoreJournalInvalid,
    UnverifiedArtifacts
}
