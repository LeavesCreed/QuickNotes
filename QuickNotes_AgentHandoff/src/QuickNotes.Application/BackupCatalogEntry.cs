namespace QuickNotes.Application;

public sealed record BackupCatalogEntry
{
    public required string PackagePath { get; init; }
    public required BackupManifest Manifest { get; init; }
    public required DateOnly LocalDate { get; init; }
    public required int IsoWeekYear { get; init; }
    public required int IsoWeek { get; init; }
}
