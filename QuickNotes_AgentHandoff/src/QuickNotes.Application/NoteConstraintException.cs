namespace QuickNotes.Application;

public sealed class NoteConstraintException : NoteStorageException
{
    public NoteConstraintException(
        string operation,
        Guid? noteId,
        string constraintKind,
        string message,
        Exception? inner = null)
        : base(operation, noteId, "constraint_violation", message, inner)
    {
        ConstraintKind = constraintKind;
    }

    public string ConstraintKind { get; }
}
