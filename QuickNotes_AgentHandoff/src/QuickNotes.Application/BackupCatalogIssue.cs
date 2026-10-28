namespace QuickNotes.Application;

public sealed record BackupCatalogIssue(string PackagePath, string ErrorCode, string Message);
