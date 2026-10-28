namespace QuickNotes.Application;

public enum RestoreStage
{
    Staging,
    ProtectionCreated,
    Switching,
    Verifying,
    Rollback,
    Completed,
    Failed
}
