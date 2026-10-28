namespace QuickNotes.Application;

public enum RestoreStatus
{
    Succeeded,
    PreflightFailed,
    VerificationFailed,
    ProtectionFailed,
    SwitchFailed,
    RollbackFailed
}
