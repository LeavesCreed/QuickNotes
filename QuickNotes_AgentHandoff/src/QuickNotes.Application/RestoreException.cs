namespace QuickNotes.Application;

public sealed class RestoreException : Exception
{
    public RestoreException(
        string operation,
        string errorCode,
        string message,
        Exception? inner = null,
        bool isRetryable = false)
        : base(message, inner)
    {
        Operation = operation;
        ErrorCode = errorCode;
        IsRetryable = isRetryable;
    }

    public string Operation { get; }
    public string ErrorCode { get; }
    public bool IsRetryable { get; }
}
