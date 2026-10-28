namespace QuickNotes.Application;

public sealed record RestoreError(
    RestoreErrorCode Code,
    string Message,
    bool IsRetryable = false,
    string? Diagnostics = null);
