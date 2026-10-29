namespace QuickNotes.Desktop.Editing;

public sealed record EditorSnapshot
{
    public required long Sequence { get; init; }
    public required Guid NoteId { get; init; }
    public required Guid DraftId { get; init; }
    public required bool IsNew { get; init; }
    public string? Title { get; init; }
    public required string Body { get; init; }
    public required Guid CategoryId { get; init; }
    public required IReadOnlyCollection<Guid> TagIds { get; init; }
    public bool IsPinned { get; init; }
    public QuickNotes.Domain.NoteDisplayIntent DisplayIntent { get; init; } =
        QuickNotes.Domain.NoteDisplayIntent.Visible;
    public QuickNotes.Domain.NoteColorMode ColorMode { get; init; } =
        QuickNotes.Domain.NoteColorMode.FollowCategory;
    public string? ColorHex { get; init; }
    public int? FontSize { get; init; }
    public QuickNotes.Domain.NoteWindowLayout? WindowLayout { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset ModifiedAtUtc { get; init; }
    public required long BaseVersion { get; init; }
}
