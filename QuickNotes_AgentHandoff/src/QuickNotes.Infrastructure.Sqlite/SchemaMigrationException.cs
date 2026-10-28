namespace QuickNotes.Infrastructure.Sqlite;

public sealed class SchemaMigrationException : Exception
{
    public SchemaMigrationException(
        string errorCode,
        string message,
        Exception? inner = null)
        : base(message, inner)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
        {
            throw new ArgumentException("Error code must not be empty.", nameof(errorCode));
        }

        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
