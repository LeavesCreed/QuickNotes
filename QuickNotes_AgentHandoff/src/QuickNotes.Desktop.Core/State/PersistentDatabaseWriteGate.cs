using QuickNotes.Application;
using QuickNotes.Desktop.Diagnostics;
using QuickNotes.Desktop.State;

namespace QuickNotes.Desktop.State;

public sealed class PersistentDatabaseWriteGate : IDatabaseWriteGate
{
    private readonly DatabaseWriteGate _inner = new();
    private readonly ApplicationStateStore _stateStore;
    private readonly DiagnosticLog _log;
    private readonly TimeProvider _timeProvider;
    private readonly string _reason;

    public PersistentDatabaseWriteGate(
        ApplicationStateStore stateStore,
        DiagnosticLog log,
        string reason = "restore_rollback_failed",
        TimeProvider? timeProvider = null,
        bool initiallyBlocked = false)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Write block reason must not be empty.", nameof(reason));
        }

        _reason = reason;
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (initiallyBlocked)
        {
            _inner.BlockWrites();
        }
    }

    public bool IsBlocked => _inner.IsBlocked;

    public Task<IDatabaseWriteLease> EnterWriteAsync(
        CancellationToken cancellationToken = default) =>
        _inner.EnterWriteAsync(cancellationToken);

    public Task<IDatabaseWriteLease> EnterRestoreAsync(
        CancellationToken cancellationToken = default) =>
        _inner.EnterRestoreAsync(cancellationToken);

    public void BlockWrites()
    {
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        if (!_stateStore.TryMarkWritesBlocked(_reason, now, out var error))
        {
            _log.TryWrite(
                "ERROR",
                "write_gate",
                "write block could not be persisted",
                error);
        }

        _inner.BlockWrites();
        _log.TryWrite("ERROR", "write_gate", "database writes blocked", _reason);
    }
}
