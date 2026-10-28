namespace QuickNotes.Desktop.Startup;

public enum StartupMode
{
    FirstRunInitialized,
    Ready,
    NeedsRecovery,
    WriteBlocked,
    ReadOnlyDiagnostic
}
