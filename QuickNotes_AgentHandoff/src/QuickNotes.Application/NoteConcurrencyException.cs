using System.Globalization;

namespace QuickNotes.Application;

public sealed class NoteConcurrencyException : NoteStorageException
{
    public NoteConcurrencyException(
        Guid noteId,
        long expectedVersion,
        long? actualVersion,
        string operation = "update",
        Exception? inner = null)
        : base(
            operation,
            noteId,
            "concurrency_conflict",
            $"Note {noteId} has a concurrency conflict during {operation}: expected version {expectedVersion}, actual version {FormatVersion(actualVersion)}.",
            inner)
    {
        ExpectedVersion = expectedVersion;
        ActualVersion = actualVersion;
    }

    public long ExpectedVersion { get; }
    public long? ActualVersion { get; }

    private static string FormatVersion(long? actualVersion) =>
        actualVersion?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
}
