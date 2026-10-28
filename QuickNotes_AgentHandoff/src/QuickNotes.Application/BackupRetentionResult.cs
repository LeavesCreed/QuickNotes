namespace QuickNotes.Application;

public sealed record BackupRetentionResult(
    IReadOnlyList<string> DeletedPackagePaths,
    IReadOnlyList<BackupRetentionWarning> Warnings);
