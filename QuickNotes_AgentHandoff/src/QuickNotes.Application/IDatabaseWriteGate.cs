namespace QuickNotes.Application;

public interface IDatabaseWriteGate
{
    bool IsBlocked { get; }

    Task<IDatabaseWriteLease> EnterWriteAsync(
        CancellationToken cancellationToken = default);

    Task<IDatabaseWriteLease> EnterRestoreAsync(
        CancellationToken cancellationToken = default);

    void BlockWrites();
}

public interface IDatabaseWriteLease : IAsyncDisposable
{
}
