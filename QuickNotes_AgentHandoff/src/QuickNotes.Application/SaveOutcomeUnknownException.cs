namespace QuickNotes.Application;

public sealed class SaveOutcomeUnknownException : NoteStorageException
{
    public SaveOutcomeUnknownException(
        string operation,
        Guid noteId,
        long expectedVersion,
        Exception inner)
        : base(
            operation,
            noteId,
            "commit_outcome_unknown",
            $"Commit outcome for note {noteId} during {operation} is unknown.",
            inner)
    {
        ExpectedVersion = expectedVersion;
    }

    public long ExpectedVersion { get; }
}
