namespace StickyNotes.Application;

public sealed class NoteConcurrencyException : Exception
{
    public NoteConcurrencyException(Guid noteId, long expectedVersion)
        : base($"Note {noteId} has a concurrency conflict for expected version {expectedVersion}.")
    {
        NoteId = noteId;
        ExpectedVersion = expectedVersion;
    }

    public Guid NoteId { get; }
    public long ExpectedVersion { get; }
}
