namespace QuickNotes.Application;

public sealed class NoopDatabaseWriteGate : IDatabaseWriteGate
{
    public static NoopDatabaseWriteGate Instance { get; } = new();

    private volatile bool _blocked;

    public bool IsBlocked => _blocked;

    public Task<IDatabaseWriteLease> EnterWriteAsync(
        CancellationToken cancellationToken = default) =>
        EnterAsync();

    public Task<IDatabaseWriteLease> EnterRestoreAsync(
        CancellationToken cancellationToken = default) =>
        EnterAsync();

    public void BlockWrites() => _blocked = true;

    private Task<IDatabaseWriteLease> EnterAsync()
    {
        if (_blocked)
        {
            throw new DatabaseWriteBlockedException();
        }

        return Task.FromResult<IDatabaseWriteLease>(NoopLease.Instance);
    }

    private sealed class NoopLease : IDatabaseWriteLease
    {
        public static NoopLease Instance { get; } = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
