using StickyNotes.Domain;

namespace StickyNotes.Application;

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
    public required DateTimeOffset CapturedAtUtc { get; init; }
}
