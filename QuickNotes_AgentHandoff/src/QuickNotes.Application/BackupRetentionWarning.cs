namespace QuickNotes.Application;

public sealed record BackupRetentionWarning(
    string PackagePath,
    string ErrorCode,
    string Message);
