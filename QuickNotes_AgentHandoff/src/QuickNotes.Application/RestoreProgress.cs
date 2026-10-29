namespace QuickNotes.Application;

public enum RestoreProgressStage
{
    Preflight,
    ProtectionSnapshot,
    Staging,
    Migrating,
    Switching,
    Verifying,
    Completed,
    Failed,
    RolledBack
}

public sealed record RestoreProgress(
    RestoreProgressStage Stage,
    string Message,
    bool IsCancelable);
