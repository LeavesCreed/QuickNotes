namespace QuickNotes.Domain;

public sealed record NoteTag
{
    public required Guid NoteId { get; init; }
    public required Guid TagId { get; init; }
}
