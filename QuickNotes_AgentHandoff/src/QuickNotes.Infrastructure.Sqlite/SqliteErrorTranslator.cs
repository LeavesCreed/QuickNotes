using Microsoft.Data.Sqlite;
using QuickNotes.Application;

namespace QuickNotes.Infrastructure.Sqlite;

internal static class SqliteErrorTranslator
{
    private const int Busy = 5;
    private const int Locked = 6;
    private const int ReadOnly = 8;
    private const int Interrupt = 9;
    private const int IoErr = 10;
    private const int Corrupt = 11;
    private const int Full = 13;
    private const int CantOpen = 14;
    private const int Constraint = 19;
    private const int NotADb = 26;

    private const int ConstraintForeignKey = 787;
    private const int ConstraintCheck = 275;
    private const int ConstraintNotNull = 1299;
    private const int ConstraintUnique = 2067;
    private const int ConstraintPrimaryKey = 1555;

    public static Exception Translate(
        SqliteException exception,
        string operation,
        Guid? noteId,
        CancellationToken cancellationToken)
    {
        var extended = exception.SqliteExtendedErrorCode;
        var primary = extended != 0 ? extended & 0xFF : exception.SqliteErrorCode;

        if (primary == Interrupt && cancellationToken.IsCancellationRequested)
        {
            return new OperationCanceledException(
                $"SQLite operation {Describe(operation, noteId)} was interrupted by cancellation.",
                exception,
                cancellationToken);
        }

        return primary switch
        {
            Busy => CreateUnavailable(operation, noteId, "sqlite_busy", isRetryable: true, extended, exception),
            Locked => CreateUnavailable(operation, noteId, "sqlite_locked", isRetryable: true, extended, exception),
            Interrupt => CreateUnavailable(operation, noteId, "sqlite_interrupt", isRetryable: false, extended, exception),
            ReadOnly => CreateUnavailable(operation, noteId, "sqlite_readonly", isRetryable: false, extended, exception),
            IoErr => CreateUnavailable(operation, noteId, "sqlite_io", isRetryable: false, extended, exception),
            Corrupt => CreateUnavailable(operation, noteId, "sqlite_corrupt", isRetryable: false, extended, exception),
            Full => CreateUnavailable(operation, noteId, "sqlite_full", isRetryable: false, extended, exception),
            CantOpen => CreateUnavailable(operation, noteId, "sqlite_cantopen", isRetryable: false, extended, exception),
            NotADb => CreateUnavailable(operation, noteId, "sqlite_notadb", isRetryable: false, extended, exception),
            Constraint => CreateConstraint(operation, noteId, extended, exception),
            _ => CreateUnavailable(operation, noteId, "sqlite_error", isRetryable: false, extended, exception)
        };
    }

    private static StorageUnavailableException CreateUnavailable(
        string operation,
        Guid? noteId,
        string errorCode,
        bool isRetryable,
        int extended,
        SqliteException inner)
    {
        var message = $"SQLite error {errorCode} (extended {extended}) during {Describe(operation, noteId)}.";
        return new StorageUnavailableException(operation, noteId, errorCode, isRetryable, message, inner);
    }

    private static NoteConstraintException CreateConstraint(
        string operation,
        Guid? noteId,
        int extended,
        SqliteException inner)
    {
        var kind = extended switch
        {
            ConstraintForeignKey => "foreign_key",
            ConstraintCheck => "check",
            ConstraintNotNull => "not_null",
            ConstraintUnique => "unique",
            ConstraintPrimaryKey => "primary_key",
            _ => "generic"
        };
        var message = $"SQLite constraint {kind} during {Describe(operation, noteId)} (extended {extended}).";
        return new NoteConstraintException(operation, noteId, kind, message, inner);
    }

    private static string Describe(string operation, Guid? noteId) =>
        noteId is null ? operation : $"{operation} for note {noteId}";
}
