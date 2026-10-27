using StickyNotes.Domain;

namespace StickyNotes.Application;

public sealed record SaveResult
{
    public required SaveStatus Status { get; init; }
    public required Guid NoteId { get; init; }
    public Note? SavedNote { get; init; }
    public Note? CurrentNote { get; init; }
    public long? CurrentVersion { get; init; }
    public SaveError? Error { get; init; }
    public Guid? DraftId { get; init; }
    public DraftDisposition DraftDisposition { get; init; }
    public SaveError? DraftError { get; init; }
    public int Attempts { get; init; }
}
