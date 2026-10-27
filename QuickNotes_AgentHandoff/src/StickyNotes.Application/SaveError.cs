namespace StickyNotes.Application;

public sealed record SaveError(
    SaveErrorCode Code,
    string Message,
    bool IsRetryable = false,
    string? Diagnostics = null);
