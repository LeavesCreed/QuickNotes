using QuickNotes.Domain;

namespace QuickNotes.Application;

public enum NoteSortOrder
{
    ModifiedDescending,
    ModifiedAscending,
    CreatedDescending,
    TitleAscending
}

public sealed record NoteQuery
{
    public NoteLifecycle? Lifecycle { get; init; }
    public Guid? CategoryId { get; init; }
    public IReadOnlyCollection<Guid> AnyTagIds { get; init; } = Array.Empty<Guid>();
    public string? SearchText { get; init; }
    public bool IncludeArchived { get; init; }
    public NoteSortOrder Sort { get; init; } = NoteSortOrder.ModifiedDescending;
}
