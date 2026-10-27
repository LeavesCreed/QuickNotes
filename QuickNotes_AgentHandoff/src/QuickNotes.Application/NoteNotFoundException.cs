namespace QuickNotes.Application;

public sealed class NoteNotFoundException : NoteStorageException
{
    public NoteNotFoundException(Guid noteId, string operation, Exception? inner = null)
        : base(
            operation,
            noteId,
            "note_not_found",
            $"Note {noteId} was not found during {operation}.",
            inner)
    {
    }
}
