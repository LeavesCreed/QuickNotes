namespace QuickNotes.Desktop.Editing;

public sealed record DraftRestoreBoundary
{
    public const int CurrentFormatVersion = 1;

    public required int FormatVersion { get; init; }
    public required Guid RestoreId { get; init; }
    public required DateTimeOffset RestoredAtUtc { get; init; }
    public required IReadOnlyList<Guid> DraftIds { get; init; }
}
