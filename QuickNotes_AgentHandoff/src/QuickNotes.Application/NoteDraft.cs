using QuickNotes.Domain;

namespace QuickNotes.Application;

public sealed record NoteDraft
{
    public required Guid DraftId { get; init; }
    public required Guid NoteId { get; init; }
    public long BaseVersion { get; init; }
    public string? Title { get; init; }
    public required string Body { get; init; }
    public required Guid CategoryId { get; init; }
    public IReadOnlyCollection<Guid> TagIds { get; init; } = Array.Empty<Guid>();
    public bool IsPinned { get; init; }
    public NoteDisplayIntent DisplayIntent { get; init; } = NoteDisplayIntent.Visible;
    public NoteColorMode ColorMode { get; init; } = NoteColorMode.FollowCategory;
    public string? ColorHex { get; init; }
    public int? FontSize { get; init; }
    public NoteWindowLayout? WindowLayout { get; init; }
    public required DateTimeOffset CapturedAtUtc { get; init; }
}
