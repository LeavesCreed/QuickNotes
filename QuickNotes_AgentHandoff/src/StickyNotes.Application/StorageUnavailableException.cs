namespace StickyNotes.Application;

public sealed class StorageUnavailableException : NoteStorageException
{
    public StorageUnavailableException(
        string operation,
        Guid? noteId,
        string errorCode,
        bool isRetryable,
        string message,
        Exception? inner = null)
        : base(operation, noteId, errorCode, message, inner)
    {
        IsRetryable = isRetryable;
    }

    public bool IsRetryable { get; }
}
