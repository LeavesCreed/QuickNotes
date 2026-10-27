namespace QuickNotes.Application;

public sealed class DraftStoreException : Exception
{
    public DraftStoreException(
        string operation,
        Guid? draftId,
        string errorCode,
        string message,
        Exception? inner = null)
        : base(message, inner)
    {
        Operation = operation;
        DraftId = draftId;
        ErrorCode = errorCode;
    }

    public Guid? DraftId { get; }
    public string Operation { get; }
    public string ErrorCode { get; }
}
