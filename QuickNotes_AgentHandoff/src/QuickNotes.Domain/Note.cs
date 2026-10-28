namespace QuickNotes.Domain;

public sealed record Note
{
    public required Guid Id { get; init; }
    public string? Title { get; init; }
    public required string Body { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset ModifiedAtUtc { get; init; }
    public long Version { get; init; } = 1;
    public NoteLifecycle Lifecycle { get; init; } = NoteLifecycle.Active;
    public required Guid CategoryId { get; init; }
    public bool IsPinned { get; init; }
    public NoteDisplayIntent DisplayIntent { get; init; } = NoteDisplayIntent.Visible;
    public NoteColorMode ColorMode { get; init; } = NoteColorMode.FollowCategory;
    public string? ColorHex { get; init; }
    public int? FontSize { get; init; }
    public NoteWindowLayout? WindowLayout { get; init; }
}
