namespace StickyNotes.Domain;

public sealed record Tag
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
}
