namespace QuickNotes.Application;

public sealed class DatabaseWriteBlockedException : Exception
{
    public DatabaseWriteBlockedException()
        : base("Database writes are blocked after a restore rollback failure.")
    {
    }
}
