namespace QuickNotes.Application;

public sealed record BackupError(
    BackupErrorCode Code,
    string Message,
    bool IsRetryable = false,
    string? Diagnostics = null);
