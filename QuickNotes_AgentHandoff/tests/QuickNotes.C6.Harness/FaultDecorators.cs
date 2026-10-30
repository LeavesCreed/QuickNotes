using QuickNotes.Application;
using QuickNotes.Domain;

namespace QuickNotes.C6.Harness;

/// <summary>
/// Test-only fault composition modes. They exist only in the C6 harness and
/// are never read by production code.
/// </summary>
public enum HarnessFaultMode
{
    None,
    DiskFull,
    Offline
}

/// <summary>
/// Wraps the real note repository and injects a storage failure on writes.
/// Reads always pass through so the harness can verify what survived the
/// fault. The exception mirrors the production SQLite translator so
/// <see cref="NoteSaveCoordinator"/> exercises its real failure path.
/// </summary>
public sealed class FaultingNoteRepository : INoteRepository
{
    private readonly INoteRepository _inner;
    private readonly HarnessFaultMode _mode;

    public FaultingNoteRepository(INoteRepository inner, HarnessFaultMode mode)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _mode = mode;
    }

    public Task<Note> CreateAsync(
        Note note,
        IReadOnlyCollection<Guid> tagIds,
        CancellationToken cancellationToken = default)
    {
        ThrowIfFaulted("create", note.Id);
        return _inner.CreateAsync(note, tagIds, cancellationToken);
    }

    public Task<Note?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        _inner.GetAsync(id, cancellationToken);

    public Task<IReadOnlyList<Note>> ListAsync(
        NoteLifecycle lifecycle,
        CancellationToken cancellationToken = default) =>
        _inner.ListAsync(lifecycle, cancellationToken);

    public Task<IReadOnlyList<Note>> QueryAsync(
        NoteQuery query,
        CancellationToken cancellationToken = default) =>
        _inner.QueryAsync(query, cancellationToken);

    public Task<IReadOnlyList<Guid>> GetTagIdsAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        _inner.GetTagIdsAsync(id, cancellationToken);

    public Task<Note> UpdateAsync(
        Note note,
        long expectedVersion,
        IReadOnlyCollection<Guid> tagIds,
        CancellationToken cancellationToken = default)
    {
        ThrowIfFaulted("update", note.Id);
        return _inner.UpdateAsync(note, expectedVersion, tagIds, cancellationToken);
    }

    public Task<Note> SetLifecycleAsync(
        Guid id,
        NoteLifecycle lifecycle,
        long expectedVersion,
        DateTimeOffset modifiedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ThrowIfFaulted("set_lifecycle", id);
        return _inner.SetLifecycleAsync(id, lifecycle, expectedVersion, modifiedAtUtc, cancellationToken);
    }

    public Task<Note> SetLifecycleAndDisplayAsync(
        Guid id,
        NoteLifecycle lifecycle,
        NoteDisplayIntent displayIntent,
        long expectedVersion,
        DateTimeOffset modifiedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ThrowIfFaulted("set_lifecycle_and_display", id);
        return _inner.SetLifecycleAndDisplayAsync(
            id,
            lifecycle,
            displayIntent,
            expectedVersion,
            modifiedAtUtc,
            cancellationToken);
    }

    public Task DeleteAsync(
        Guid id,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ThrowIfFaulted("delete", id);
        return _inner.DeleteAsync(id, expectedVersion, cancellationToken);
    }

    private void ThrowIfFaulted(string operation, Guid? noteId)
    {
        if (_mode == HarnessFaultMode.None)
        {
            return;
        }

        var errorCode = _mode == HarnessFaultMode.DiskFull ? "sqlite_full" : "sqlite_cantopen";
        throw new StorageUnavailableException(
            operation,
            noteId,
            errorCode,
            isRetryable: false,
            $"Injected C6 {_mode} fault during {operation}.");
    }
}

/// <summary>
/// Wraps the real ZIP package store and fails at publish time. The backup
/// coordinator still creates and verifies a snapshot first, so this exercises
/// the real "package never became final" boundary instead of replacing the
/// whole backup service with a fake.
/// </summary>
public sealed class FaultingBackupPackageStore : IBackupPackageStore
{
    private readonly IBackupPackageStore _inner;
    private readonly HarnessFaultMode _mode;

    public FaultingBackupPackageStore(IBackupPackageStore inner, HarnessFaultMode mode)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _mode = mode;
    }

    public Task<BackupPackageInfo> PublishAsync(
        string snapshotDatabasePath,
        long schemaVersion,
        BackupRequest request,
        DateTimeOffset createdAtUtc,
        DateTimeOffset createdAtLocal,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_mode == HarnessFaultMode.None)
        {
            return _inner.PublishAsync(
                snapshotDatabasePath,
                schemaVersion,
                request,
                createdAtUtc,
                createdAtLocal,
                cancellationToken);
        }

        var errorCode = _mode == HarnessFaultMode.Offline
            ? BackupErrorCodes.DestinationUnavailable
            : BackupErrorCodes.PackageWriteFailed;
        throw new BackupException(
            "package",
            errorCode,
            $"Injected C6 {_mode} fault before publishing the backup package.",
            new IOException(_mode == HarnessFaultMode.DiskFull
                ? "The disk is full."
                : "The backup destination is offline."));
    }
}
