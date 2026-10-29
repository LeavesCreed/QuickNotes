using QuickNotes.Domain;

namespace QuickNotes.Application;

public sealed class NoteManagementService
{
    private readonly INoteRepository _notes;
    private readonly ICatalogRepository _catalog;
    private readonly IDatabaseWriteGate _writeGate;
    private readonly TimeProvider _timeProvider;

    public NoteManagementService(
        INoteRepository notes,
        ICatalogRepository catalog,
        IDatabaseWriteGate writeGate,
        TimeProvider? timeProvider = null)
    {
        _notes = notes ?? throw new ArgumentNullException(nameof(notes));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _writeGate = writeGate ?? throw new ArgumentNullException(nameof(writeGate));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<IReadOnlyList<Note>> QueryAsync(
        NoteQuery query,
        CancellationToken cancellationToken = default) =>
        _notes.QueryAsync(query, cancellationToken);

    public async Task<Note> MoveToCategoryAsync(
        Guid noteId,
        Guid categoryId,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await _writeGate.EnterWriteAsync(cancellationToken).ConfigureAwait(false);
        return await MoveToCategoryCoreAsync(noteId, categoryId, expectedVersion, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<BatchOperationResult> MoveToCategoryAsync(
        IReadOnlyCollection<Guid> noteIds,
        Guid categoryId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(noteIds);
        await using var lease = await _writeGate.EnterWriteAsync(cancellationToken).ConfigureAwait(false);
        _ = await _catalog.GetCategoryAsync(categoryId, cancellationToken).ConfigureAwait(false)
            ?? throw new CatalogNotFoundException(categoryId, "batch_move_note_category");
        return await RunBatchAsync(
                noteIds,
                (noteId, version) => MoveToCategoryCoreAsync(
                    noteId,
                    categoryId,
                    version,
                    cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Note> SetPinnedAsync(
        Guid noteId,
        bool isPinned,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await _writeGate.EnterWriteAsync(cancellationToken).ConfigureAwait(false);
        var note = await GetRequiredAsync(noteId, cancellationToken).ConfigureAwait(false);
        var tags = await _notes.GetTagIdsAsync(noteId, cancellationToken).ConfigureAwait(false);
        return await _notes.UpdateAsync(
                note with { IsPinned = isPinned, ModifiedAtUtc = Now() },
                expectedVersion,
                tags,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Note> AddTagsAsync(
        Guid noteId,
        IReadOnlyCollection<Guid> tagIds,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tagIds);
        await using var lease = await _writeGate.EnterWriteAsync(cancellationToken).ConfigureAwait(false);
        return await AddTagsCoreAsync(noteId, tagIds, expectedVersion, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<BatchOperationResult> AddTagsAsync(
        IReadOnlyCollection<Guid> noteIds,
        IReadOnlyCollection<Guid> tagIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(noteIds);
        ArgumentNullException.ThrowIfNull(tagIds);
        await using var lease = await _writeGate.EnterWriteAsync(cancellationToken).ConfigureAwait(false);
        foreach (var tagId in tagIds.Distinct())
        {
            _ = await _catalog.GetTagAsync(tagId, cancellationToken).ConfigureAwait(false)
                ?? throw new CatalogNotFoundException(tagId, "batch_add_note_tag");
        }

        return await RunBatchAsync(
                noteIds,
                (noteId, version) => AddTagsCoreAsync(noteId, tagIds, version, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Note> RemoveTagsAsync(
        Guid noteId,
        IReadOnlyCollection<Guid> tagIds,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tagIds);
        await using var lease = await _writeGate.EnterWriteAsync(cancellationToken).ConfigureAwait(false);
        return await RemoveTagsCoreAsync(noteId, tagIds, expectedVersion, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<BatchOperationResult> RemoveTagsAsync(
        IReadOnlyCollection<Guid> noteIds,
        IReadOnlyCollection<Guid> tagIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(noteIds);
        ArgumentNullException.ThrowIfNull(tagIds);
        await using var lease = await _writeGate.EnterWriteAsync(cancellationToken).ConfigureAwait(false);
        return await RunBatchAsync(
                noteIds,
                (noteId, version) => RemoveTagsCoreAsync(noteId, tagIds, version, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Note> ArchiveAsync(
        Guid noteId,
        long expectedVersion,
        CancellationToken cancellationToken = default) =>
        await ChangeLifecycleAsync(
                noteId,
                NoteLifecycle.Archived,
                NoteDisplayIntent.Hidden,
                expectedVersion,
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<Note> UnarchiveAsync(
        Guid noteId,
        long expectedVersion,
        CancellationToken cancellationToken = default) =>
        await ChangeLifecycleAsync(
                noteId,
                NoteLifecycle.Active,
                NoteDisplayIntent.Hidden,
                expectedVersion,
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<Note> MoveToTrashAsync(
        Guid noteId,
        long expectedVersion,
        CancellationToken cancellationToken = default) =>
        await ChangeLifecycleAsync(
                noteId,
                NoteLifecycle.Trash,
                NoteDisplayIntent.Hidden,
                expectedVersion,
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<Note> RestoreFromTrashAsync(
        Guid noteId,
        long expectedVersion,
        CancellationToken cancellationToken = default) =>
        await ChangeLifecycleAsync(
                noteId,
                NoteLifecycle.Active,
                NoteDisplayIntent.Hidden,
                expectedVersion,
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<BatchOperationResult> ArchiveAsync(
        IReadOnlyCollection<Guid> noteIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(noteIds);
        await using var lease = await _writeGate.EnterWriteAsync(cancellationToken).ConfigureAwait(false);
        return await RunBatchAsync(
                noteIds,
                (noteId, version) => ChangeLifecycleCoreAsync(
                    noteId,
                    NoteLifecycle.Archived,
                    NoteDisplayIntent.Hidden,
                    version,
                    cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<BatchOperationResult> MoveToTrashAsync(
        IReadOnlyCollection<Guid> noteIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(noteIds);
        await using var lease = await _writeGate.EnterWriteAsync(cancellationToken).ConfigureAwait(false);
        return await RunBatchAsync(
                noteIds,
                (noteId, version) => ChangeLifecycleCoreAsync(
                    noteId,
                    NoteLifecycle.Trash,
                    NoteDisplayIntent.Hidden,
                    version,
                    cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task DeletePermanentlyAsync(
        Guid noteId,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await _writeGate.EnterWriteAsync(cancellationToken).ConfigureAwait(false);
        await _notes.DeleteAsync(noteId, expectedVersion, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Note> CreateCopyAsync(
        Guid noteId,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await _writeGate.EnterWriteAsync(cancellationToken).ConfigureAwait(false);
        var source = await GetRequiredAsync(noteId, cancellationToken).ConfigureAwait(false);
        var tags = await _notes.GetTagIdsAsync(noteId, cancellationToken).ConfigureAwait(false);
        var title = await BuildCopyTitleAsync(source.Title, cancellationToken).ConfigureAwait(false);
        var now = Now();
        var layout = source.WindowLayout is { } existing
            ? existing with { Left = existing.Left + 28, Top = existing.Top + 28 }
            : null;
        var copy = source with
        {
            Id = Guid.NewGuid(),
            Title = title,
            CreatedAtUtc = now,
            ModifiedAtUtc = now,
            Version = 1,
            Lifecycle = NoteLifecycle.Active,
            DisplayIntent = NoteDisplayIntent.Visible,
            IsPinned = false,
            WindowLayout = layout
        };
        return await _notes.CreateAsync(copy, tags, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Note> ChangeLifecycleAsync(
        Guid noteId,
        NoteLifecycle lifecycle,
        NoteDisplayIntent displayIntent,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        await using var lease = await _writeGate.EnterWriteAsync(cancellationToken).ConfigureAwait(false);
        return await ChangeLifecycleCoreAsync(
                noteId,
                lifecycle,
                displayIntent,
                expectedVersion,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private Task<Note> ChangeLifecycleCoreAsync(
        Guid noteId,
        NoteLifecycle lifecycle,
        NoteDisplayIntent displayIntent,
        long expectedVersion,
        CancellationToken cancellationToken) =>
        _notes.SetLifecycleAndDisplayAsync(
            noteId,
            lifecycle,
            displayIntent,
            expectedVersion,
            Now(),
            cancellationToken);

    private async Task<Note> MoveToCategoryCoreAsync(
        Guid noteId,
        Guid categoryId,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        _ = await _catalog.GetCategoryAsync(categoryId, cancellationToken).ConfigureAwait(false)
            ?? throw new CatalogNotFoundException(categoryId, "move_note_category");
        var note = await GetRequiredAsync(noteId, cancellationToken).ConfigureAwait(false);
        var tags = await _notes.GetTagIdsAsync(noteId, cancellationToken).ConfigureAwait(false);
        return await _notes.UpdateAsync(
                note with { CategoryId = categoryId, ModifiedAtUtc = Now() },
                expectedVersion,
                tags,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<Note> AddTagsCoreAsync(
        Guid noteId,
        IReadOnlyCollection<Guid> tagIds,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        foreach (var tagId in tagIds.Distinct())
        {
            _ = await _catalog.GetTagAsync(tagId, cancellationToken).ConfigureAwait(false)
                ?? throw new CatalogNotFoundException(tagId, "add_note_tag");
        }

        var note = await GetRequiredAsync(noteId, cancellationToken).ConfigureAwait(false);
        var current = await _notes.GetTagIdsAsync(noteId, cancellationToken).ConfigureAwait(false);
        var merged = current.Concat(tagIds).Distinct().ToArray();
        return await _notes.UpdateAsync(
                note with { ModifiedAtUtc = Now() },
                expectedVersion,
                merged,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<Note> RemoveTagsCoreAsync(
        Guid noteId,
        IReadOnlyCollection<Guid> tagIds,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        var note = await GetRequiredAsync(noteId, cancellationToken).ConfigureAwait(false);
        var current = await _notes.GetTagIdsAsync(noteId, cancellationToken).ConfigureAwait(false);
        var remaining = current.Where(id => !tagIds.Contains(id)).ToArray();
        return await _notes.UpdateAsync(
                note with { ModifiedAtUtc = Now() },
                expectedVersion,
                remaining,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<BatchOperationResult> RunBatchAsync(
        IReadOnlyCollection<Guid> noteIds,
        Func<Guid, long, Task<Note>> operation,
        CancellationToken cancellationToken)
    {
        var results = new List<NoteManagementItemResult>();
        foreach (var noteId in noteIds.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var note = await _notes.GetAsync(noteId, cancellationToken).ConfigureAwait(false)
                    ?? throw new NoteNotFoundException(noteId, "batch_management");
                var updated = await operation(noteId, note.Version).ConfigureAwait(false);
                results.Add(new NoteManagementItemResult(
                    noteId,
                    NoteManagementItemStatus.Succeeded,
                    updated,
                    null,
                    null));
            }
            catch (NoteConcurrencyException exception)
            {
                results.Add(new NoteManagementItemResult(
                    noteId,
                    NoteManagementItemStatus.Conflict,
                    null,
                    exception.ErrorCode,
                    exception.Message));
            }
            catch (NoteNotFoundException exception)
            {
                results.Add(new NoteManagementItemResult(
                    noteId,
                    NoteManagementItemStatus.NotFound,
                    null,
                    exception.ErrorCode,
                    exception.Message));
            }
            catch (Exception exception)
            {
                var code = exception is NoteStorageException storage
                    ? storage.ErrorCode
                    : exception.GetType().Name;
                results.Add(new NoteManagementItemResult(
                    noteId,
                    NoteManagementItemStatus.Failed,
                    null,
                    code,
                    exception.Message));
            }
        }

        return new BatchOperationResult(results);
    }

    private async Task<Note> GetRequiredAsync(
        Guid noteId,
        CancellationToken cancellationToken) =>
        await _notes.GetAsync(noteId, cancellationToken).ConfigureAwait(false)
        ?? throw new NoteNotFoundException(noteId, "management");

    private async Task<string?> BuildCopyTitleAsync(
        string? title,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var candidates = await _notes
            .QueryAsync(
                new NoteQuery
                {
                    IncludeArchived = true,
                    SearchText = title
                },
                cancellationToken)
            .ConfigureAwait(false);
        var candidate = $"{title}（副本）";
        var number = 2;
        while (candidates.Any(note => string.Equals(note.Title, candidate, StringComparison.Ordinal)))
        {
            candidate = $"{title}（副本 {number}）";
            number++;
        }

        return candidate;
    }

    private DateTimeOffset Now() => _timeProvider.GetUtcNow().ToUniversalTime();
}
