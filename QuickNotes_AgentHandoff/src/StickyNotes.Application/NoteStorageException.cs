namespace StickyNotes.Application;

public abstract class NoteStorageException : Exception
{
    protected NoteStorageException(
        string operation,
        Guid? noteId,
        string errorCode,
        string message,
        Exception? inner = null)
        : base(message, inner)
    {
        Operation = operation;
        NoteId = noteId;
        ErrorCode = errorCode;
    }

    public Guid? NoteId { get; }
    public string Operation { get; }
    public string ErrorCode { get; }
}
