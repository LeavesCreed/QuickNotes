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
/// The checkpoint marker names shared by the harness scenarios, the external
/// kill driver, and the decorator tests. Markers are stage names only; they
/// never contain note bodies, drafts, or machine paths.
/// </summary>
public static class CheckpointNames
{
    public const string BackupBeforePublish = "backup.before_publish";
    public const string BackupAfterPublish = "backup.after_publish";
    public const string RestoreBeforeSwitch = "restore.before_switch";
    public const string RestoreAfterSwitch = "restore.after_switch";
    public const string RestoreRollback = "restore.rollback";
    public const string MigrationBefore = "migration.before";
    public const string MigrationAfter = "migration.after";
    public const string SaveAfterDraft = "save.after_draft";
    public const string SaveBeforeCommit = "save.before_commit";
    public const string SaveAfterCommit = "save.after_commit";

    public static bool IsKnownFor(HarnessScenario scenario, string? marker) =>
        !string.IsNullOrWhiteSpace(marker) && scenario switch
        {
            HarnessScenario.Backup => marker is BackupBeforePublish or BackupAfterPublish,
            HarnessScenario.Restore => marker is
                RestoreBeforeSwitch or RestoreAfterSwitch or RestoreRollback,
            HarnessScenario.Migrate => marker is MigrationBefore or MigrationAfter,
            HarnessScenario.Save => marker is SaveAfterDraft or SaveBeforeCommit or SaveAfterCommit,
            _ => false
        };
}

public sealed class CheckpointingBackupPackageStore : IBackupPackageStore
{
    private readonly IBackupPackageStore _inner;
    private readonly CheckpointFileSink _checkpoints;

    public CheckpointingBackupPackageStore(
        IBackupPackageStore inner,
        CheckpointFileSink checkpoints)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _checkpoints = checkpoints ?? throw new ArgumentNullException(nameof(checkpoints));
    }

    public async Task<BackupPackageInfo> PublishAsync(
        string snapshotDatabasePath,
        long schemaVersion,
        BackupRequest request,
        DateTimeOffset createdAtUtc,
        DateTimeOffset createdAtLocal,
        CancellationToken cancellationToken = default)
    {
        _checkpoints.Reached(CheckpointNames.BackupBeforePublish);
        var published = await _inner.PublishAsync(
                snapshotDatabasePath,
                schemaVersion,
                request,
                createdAtUtc,
                createdAtLocal,
                cancellationToken)
            .ConfigureAwait(false);
        _checkpoints.Reached(CheckpointNames.BackupAfterPublish);
        return published;
    }
}

/// <summary>
/// Writes restore stage checkpoints around the real switch and rollback. The
/// optional stage reporter lets the restore scenario keep the production
/// restore journal format up to date so a killed process is visible to the
/// startup recovery probe.
/// </summary>
public sealed class CheckpointingRestoreStore : IRestoreStore
{
    private readonly IRestoreStore _inner;
    private readonly CheckpointFileSink _checkpoints;
    private readonly Func<RestoreStage, Task>? _stageReporter;

    public CheckpointingRestoreStore(
        IRestoreStore inner,
        CheckpointFileSink checkpoints,
        Func<RestoreStage, Task>? stageReporter = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _checkpoints = checkpoints ?? throw new ArgumentNullException(nameof(checkpoints));
        _stageReporter = stageReporter;
    }

    public Task<RestoreDatabaseInfo> InspectAsync(
        string databasePath,
        CancellationToken cancellationToken = default) =>
        _inner.InspectAsync(databasePath, cancellationToken);

    public Task<string?> TryGetDatabaseHashAsync(
        string databasePath,
        CancellationToken cancellationToken = default) =>
        _inner.TryGetDatabaseHashAsync(databasePath, cancellationToken);

    public Task<RestoreDatabaseInfo> PrepareCurrentAsync(
        string currentDatabasePath,
        CancellationToken cancellationToken = default) =>
        _inner.PrepareCurrentAsync(currentDatabasePath, cancellationToken);

    public async Task SwitchAsync(
        string stagedDatabasePath,
        string currentDatabasePath,
        string rollbackDatabasePath,
        CancellationToken cancellationToken = default)
    {
        if (_stageReporter is not null)
        {
            await _stageReporter(RestoreStage.Switching).ConfigureAwait(false);
        }

        _checkpoints.Reached(CheckpointNames.RestoreBeforeSwitch);
        await _inner.SwitchAsync(
                stagedDatabasePath,
                currentDatabasePath,
                rollbackDatabasePath,
                cancellationToken)
            .ConfigureAwait(false);
        if (_stageReporter is not null)
        {
            await _stageReporter(RestoreStage.Verifying).ConfigureAwait(false);
        }

        _checkpoints.Reached(CheckpointNames.RestoreAfterSwitch);
    }

    public async Task RollbackAsync(
        string rollbackDatabasePath,
        string currentDatabasePath,
        string failedDatabasePath,
        CancellationToken cancellationToken = default)
    {
        if (_stageReporter is not null)
        {
            await _stageReporter(RestoreStage.Rollback).ConfigureAwait(false);
        }

        _checkpoints.Reached(CheckpointNames.RestoreRollback);
        await _inner.RollbackAsync(
                rollbackDatabasePath,
                currentDatabasePath,
                failedDatabasePath,
                cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// Harness-only composition for C6-K07. It delegates every restore stage and
/// reports one extra active note when the current database is inspected after
/// a successful switch, which drives the production coordinator into its real
/// rollback path without adding a production test hook.
/// </summary>
public sealed class PostSwitchVerificationFaultRestoreStore : IRestoreStore
{
    private readonly IRestoreStore _inner;
    private string? _currentDatabasePath;
    private int _mismatchServed;

    public PostSwitchVerificationFaultRestoreStore(IRestoreStore inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public async Task<RestoreDatabaseInfo> InspectAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        var info = await _inner.InspectAsync(databasePath, cancellationToken)
            .ConfigureAwait(false);
        if (_currentDatabasePath is not null
            && string.Equals(
                Path.GetFullPath(databasePath),
                _currentDatabasePath,
                StringComparison.OrdinalIgnoreCase)
            && Interlocked.Exchange(ref _mismatchServed, 1) == 0)
        {
            return info with
            {
                Counts = info.Counts with { ActiveNotes = info.Counts.ActiveNotes + 1 }
            };
        }

        return info;
    }

    public Task<string?> TryGetDatabaseHashAsync(
        string databasePath,
        CancellationToken cancellationToken = default) =>
        _inner.TryGetDatabaseHashAsync(databasePath, cancellationToken);

    public Task<RestoreDatabaseInfo> PrepareCurrentAsync(
        string currentDatabasePath,
        CancellationToken cancellationToken = default) =>
        _inner.PrepareCurrentAsync(currentDatabasePath, cancellationToken);

    public async Task SwitchAsync(
        string stagedDatabasePath,
        string currentDatabasePath,
        string rollbackDatabasePath,
        CancellationToken cancellationToken = default)
    {
        await _inner.SwitchAsync(
                stagedDatabasePath,
                currentDatabasePath,
                rollbackDatabasePath,
                cancellationToken)
            .ConfigureAwait(false);
        _currentDatabasePath = Path.GetFullPath(currentDatabasePath);
    }

    public Task RollbackAsync(
        string rollbackDatabasePath,
        string currentDatabasePath,
        string failedDatabasePath,
        CancellationToken cancellationToken = default) =>
        _inner.RollbackAsync(
            rollbackDatabasePath,
            currentDatabasePath,
            failedDatabasePath,
            cancellationToken);
}

public sealed class CheckpointingSchemaMigrator : ISchemaMigrator
{
    private readonly ISchemaMigrator _inner;
    private readonly CheckpointFileSink _checkpoints;

    public CheckpointingSchemaMigrator(
        ISchemaMigrator inner,
        CheckpointFileSink checkpoints)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _checkpoints = checkpoints ?? throw new ArgumentNullException(nameof(checkpoints));
    }

    public async Task<SchemaMigrationOutcome> MigrateToCurrentAsync(
        string databasePath,
        bool rotateIdentity,
        CancellationToken cancellationToken = default)
    {
        _checkpoints.Reached(CheckpointNames.MigrationBefore);
        var outcome = await _inner.MigrateToCurrentAsync(
                databasePath,
                rotateIdentity,
                cancellationToken)
            .ConfigureAwait(false);
        _checkpoints.Reached(CheckpointNames.MigrationAfter);
        return outcome;
    }
}

public sealed class CheckpointingNoteDraftStore : INoteDraftStore
{
    private readonly INoteDraftStore _inner;
    private readonly CheckpointFileSink _checkpoints;

    public CheckpointingNoteDraftStore(
        INoteDraftStore inner,
        CheckpointFileSink checkpoints)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _checkpoints = checkpoints ?? throw new ArgumentNullException(nameof(checkpoints));
    }

    public async Task SaveAsync(
        NoteDraft draft,
        CancellationToken cancellationToken = default)
    {
        await _inner.SaveAsync(draft, cancellationToken).ConfigureAwait(false);
        _checkpoints.Reached(CheckpointNames.SaveAfterDraft);
    }

    public Task<NoteDraft?> GetAsync(
        Guid draftId,
        CancellationToken cancellationToken = default) =>
        _inner.GetAsync(draftId, cancellationToken);

    public Task<IReadOnlyList<NoteDraft>> ListAsync(
        Guid? noteId = null,
        CancellationToken cancellationToken = default) =>
        _inner.ListAsync(noteId, cancellationToken);

    public Task DeleteAsync(
        Guid draftId,
        CancellationToken cancellationToken = default) =>
        _inner.DeleteAsync(draftId, cancellationToken);
}

/// <summary>
/// Marks the write boundary of the real save coordinator. The marker is
/// written immediately before the repository update and again immediately
/// after it returns, so a kill at <c>save.before_commit</c> cannot have
/// produced a committed note.
/// </summary>
public sealed class CheckpointingNoteRepository : INoteRepository
{
    private readonly INoteRepository _inner;
    private readonly CheckpointFileSink _checkpoints;

    public CheckpointingNoteRepository(
        INoteRepository inner,
        CheckpointFileSink checkpoints)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _checkpoints = checkpoints ?? throw new ArgumentNullException(nameof(checkpoints));
    }

    public async Task<Note> CreateAsync(
        Note note,
        IReadOnlyCollection<Guid> tagIds,
        CancellationToken cancellationToken = default)
    {
        _checkpoints.Reached(CheckpointNames.SaveBeforeCommit);
        var created = await _inner.CreateAsync(note, tagIds, cancellationToken)
            .ConfigureAwait(false);
        _checkpoints.Reached(CheckpointNames.SaveAfterCommit);
        return created;
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

    public async Task<Note> UpdateAsync(
        Note note,
        long expectedVersion,
        IReadOnlyCollection<Guid> tagIds,
        CancellationToken cancellationToken = default)
    {
        _checkpoints.Reached(CheckpointNames.SaveBeforeCommit);
        var updated = await _inner.UpdateAsync(note, expectedVersion, tagIds, cancellationToken)
            .ConfigureAwait(false);
        _checkpoints.Reached(CheckpointNames.SaveAfterCommit);
        return updated;
    }

    public Task<Note> SetLifecycleAsync(
        Guid id,
        NoteLifecycle lifecycle,
        long expectedVersion,
        DateTimeOffset modifiedAtUtc,
        CancellationToken cancellationToken = default) =>
        _inner.SetLifecycleAsync(id, lifecycle, expectedVersion, modifiedAtUtc, cancellationToken);

    public Task<Note> SetLifecycleAndDisplayAsync(
        Guid id,
        NoteLifecycle lifecycle,
        NoteDisplayIntent displayIntent,
        long expectedVersion,
        DateTimeOffset modifiedAtUtc,
        CancellationToken cancellationToken = default) =>
        _inner.SetLifecycleAndDisplayAsync(
            id,
            lifecycle,
            displayIntent,
            expectedVersion,
            modifiedAtUtc,
            cancellationToken);

    public Task DeleteAsync(
        Guid id,
        long expectedVersion,
        CancellationToken cancellationToken = default) =>
        _inner.DeleteAsync(id, expectedVersion, cancellationToken);
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
