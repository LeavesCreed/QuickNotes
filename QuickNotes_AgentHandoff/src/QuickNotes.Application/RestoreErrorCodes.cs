namespace QuickNotes.Application;

public static class RestoreErrorCodes
{
    public const string InspectFailed = "inspect_failed";
    public const string CurrentDatabaseUnavailable = "current_database_unavailable";
    public const string WalUnsupported = "wal_unsupported";
    public const string PrepareFailed = "prepare_failed";
    public const string SwitchFailed = "switch_failed";
    public const string RollbackFailed = "rollback_failed";
    public const string StagingUnavailable = "staging_unavailable";
    public const string SpaceUnavailable = "space_unavailable";
}
