namespace QuickNotes.Application;

public sealed class DatabaseWriteGate : IDatabaseWriteGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _blocked;

    public bool IsBlocked => _blocked;

    public Task<IDatabaseWriteLease> EnterWriteAsync(
        CancellationToken cancellationToken = default) =>
        EnterAsync(cancellationToken);

    public Task<IDatabaseWriteLease> EnterRestoreAsync(
        CancellationToken cancellationToken = default) =>
        EnterAsync(cancellationToken);

    public void BlockWrites() => _blocked = true;

    private async Task<IDatabaseWriteLease> EnterAsync(CancellationToken cancellationToken)
    {
        if (_blocked)
        {
            throw new DatabaseWriteBlockedException();
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (_blocked)
        {
            _gate.Release();
            throw new DatabaseWriteBlockedException();
        }

        return new Lease(_gate);
    }

    private sealed class Lease : IDatabaseWriteLease
    {
        private SemaphoreSlim? _gate;

        public Lease(SemaphoreSlim gate)
        {
            _gate = gate;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }
}
