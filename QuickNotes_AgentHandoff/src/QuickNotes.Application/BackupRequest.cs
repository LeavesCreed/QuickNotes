namespace QuickNotes.Application;

public sealed record BackupRequest
{
    public required string BackupDirectory { get; init; }
    public BackupType Type { get; init; } = BackupType.Manual;
}
