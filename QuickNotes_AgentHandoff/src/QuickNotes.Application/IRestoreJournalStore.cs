namespace QuickNotes.Application;

public enum RestoreJournalReadStatus
{
    Found,
    NotFound,
    Invalid
}

public sealed record RestoreJournalReadResult
{
    public required RestoreJournalReadStatus Status { get; init; }
    public RestoreJournal? Journal { get; init; }
    public string? Error { get; init; }

    public static RestoreJournalReadResult Found(RestoreJournal journal) => new()
    {
        Status = RestoreJournalReadStatus.Found,
        Journal = journal
    };

    public static RestoreJournalReadResult NotFound() => new()
    {
        Status = RestoreJournalReadStatus.NotFound
    };

    public static RestoreJournalReadResult Invalid(string error) => new()
    {
        Status = RestoreJournalReadStatus.Invalid,
        Error = error
    };
}

public interface IRestoreJournalStore
{
    Task<RestoreJournalReadResult> ReadAsync(CancellationToken cancellationToken = default);

    Task WriteAsync(RestoreJournal journal, CancellationToken cancellationToken = default);
}
