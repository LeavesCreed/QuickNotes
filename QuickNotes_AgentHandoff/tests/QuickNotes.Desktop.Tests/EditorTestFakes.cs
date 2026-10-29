using QuickNotes.Application;
using QuickNotes.Desktop.Editing;
using QuickNotes.Domain;

namespace QuickNotes.Desktop.Tests;

internal sealed class FakeNoteRepository : INoteRepository
{
    public Dictionary<Guid, Note> Notes { get; } = new();
    public Dictionary<Guid, IReadOnlyList<Guid>> Tags { get; } = new();
    public Func<Guid, CancellationToken, Task<Note?>>? GetHandler { get; set; }

    public void Add(Note note, IEnumerable<Guid>? tagIds = null)
    {
        Notes[note.Id] = note;
        Tags[note.Id] = (tagIds ?? Array.Empty<Guid>()).ToArray();
    }

    public Task<Note> CreateAsync(
        Note note,
        IReadOnlyCollection<Guid> tagIds,
        CancellationToken cancellationToken = default)
    {
        var created = note with { Version = 1 };
        Add(created, tagIds);
        return Task.FromResult(created);
    }

    public Task<Note?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        GetHandler is not null
            ? GetHandler(id, cancellationToken)
            : Task.FromResult(Notes.TryGetValue(id, out var note) ? note : null);

    public Task<IReadOnlyList<Note>> ListAsync(
        NoteLifecycle lifecycle,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Note> result = Notes.Values
            .Where(note => note.Lifecycle == lifecycle)
            .OrderByDescending(note => note.ModifiedAtUtc)
            .ToArray();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<Note>> QueryAsync(
        NoteQuery query,
        CancellationToken cancellationToken = default)
    {
        var result = Notes.Values.Where(note =>
            (query.Lifecycle is { } lifecycle && note.Lifecycle == lifecycle)
            || (query.Lifecycle is null && note.Lifecycle == NoteLifecycle.Active)
            || (query.Lifecycle is null && query.IncludeArchived && note.Lifecycle == NoteLifecycle.Archived));
        if (query.CategoryId is { } categoryId)
        {
            result = result.Where(note => note.CategoryId == categoryId);
        }

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            result = result.Where(note =>
                (note.Title?.Contains(query.SearchText, StringComparison.OrdinalIgnoreCase) == true)
                || note.Body.Contains(query.SearchText, StringComparison.OrdinalIgnoreCase));
        }

        IReadOnlyList<Note> ordered = result
            .OrderByDescending(note => note.ModifiedAtUtc)
            .ToArray();
        return Task.FromResult(ordered);
    }

    public Task<IReadOnlyList<Guid>> GetTagIdsAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Tags.TryGetValue(id, out var tags)
            ? tags
            : (IReadOnlyList<Guid>)Array.Empty<Guid>());

    public Task<Note> UpdateAsync(
        Note note,
        long expectedVersion,
        IReadOnlyCollection<Guid> tagIds,
        CancellationToken cancellationToken = default)
    {
        var updated = note with { Version = expectedVersion + 1 };
        Add(updated, tagIds);
        return Task.FromResult(updated);
    }

    public Task<Note> SetLifecycleAsync(
        Guid id,
        NoteLifecycle lifecycle,
        long expectedVersion,
        DateTimeOffset modifiedAtUtc,
        CancellationToken cancellationToken = default) =>
        SetLifecycleAndDisplayAsync(
            id,
            lifecycle,
            NoteDisplayIntent.Hidden,
            expectedVersion,
            modifiedAtUtc,
            cancellationToken);

    public Task<Note> SetLifecycleAndDisplayAsync(
        Guid id,
        NoteLifecycle lifecycle,
        NoteDisplayIntent displayIntent,
        long expectedVersion,
        DateTimeOffset modifiedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var current = Notes[id];
        var updated = current with
        {
            Lifecycle = lifecycle,
            DisplayIntent = displayIntent,
            Version = expectedVersion + 1,
            ModifiedAtUtc = modifiedAtUtc
        };
        Notes[id] = updated;
        return Task.FromResult(updated);
    }

    public Task DeleteAsync(
        Guid id,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        Notes.Remove(id);
        Tags.Remove(id);
        return Task.CompletedTask;
    }
}

internal sealed class FakeDraftStore : INoteDraftStore
{
    public Dictionary<Guid, NoteDraft> Drafts { get; } = new();
    public bool FailOnSave { get; set; }
    public bool FailOnDelete { get; set; }
    public int SaveCalls { get; private set; }
    public int DeleteCalls { get; private set; }
    public TaskCompletionSource SavedOnce { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task SaveAsync(NoteDraft draft, CancellationToken cancellationToken = default)
    {
        SaveCalls++;
        if (FailOnSave)
        {
            throw new DraftStoreException("save", draft.DraftId, "draft_io", "simulated draft save failure");
        }

        Drafts[draft.DraftId] = draft;
        SavedOnce.TrySetResult();
        return Task.CompletedTask;
    }

    public Task<NoteDraft?> GetAsync(Guid draftId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Drafts.TryGetValue(draftId, out var draft) ? draft : null);

    public Task<IReadOnlyList<NoteDraft>> ListAsync(
        Guid? noteId = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<NoteDraft> result = Drafts.Values
            .Where(draft => noteId is null || draft.NoteId == noteId)
            .ToArray();
        return Task.FromResult(result);
    }

    public Task DeleteAsync(Guid draftId, CancellationToken cancellationToken = default)
    {
        DeleteCalls++;
        if (FailOnDelete)
        {
            throw new DraftStoreException("delete", draftId, "draft_io", "simulated draft delete failure");
        }

        Drafts.Remove(draftId);
        return Task.CompletedTask;
    }
}

internal sealed class FakeSaveCoordinator : INoteSaveCoordinator
{
    public Func<Note, IReadOnlyCollection<Guid>, Guid?, CancellationToken, Task<SaveResult>>? CreateHandler { get; set; }
    public Func<Note, long, IReadOnlyCollection<Guid>, Guid?, CancellationToken, Task<SaveResult>>? UpdateHandler { get; set; }
    public List<SaveCall> Calls { get; } = new();
    public int CreateCalls => Calls.Count(call => call.IsCreate);
    public int UpdateCalls => Calls.Count(call => !call.IsCreate);

    public Task<SaveResult> CreateAsync(
        Note note,
        IReadOnlyCollection<Guid> tagIds,
        Guid? draftId = null,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(new SaveCall(true, note, 0, draftId));
        return CreateHandler?.Invoke(note, tagIds, draftId, cancellationToken)
            ?? Task.FromResult(SavedResult(note with { Version = 1 }, draftId));
    }

    public Task<SaveResult> UpdateAsync(
        Note note,
        long expectedVersion,
        IReadOnlyCollection<Guid> tagIds,
        Guid? draftId = null,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(new SaveCall(false, note, expectedVersion, draftId));
        return UpdateHandler?.Invoke(note, expectedVersion, tagIds, draftId, cancellationToken)
            ?? Task.FromResult(SavedResult(note with { Version = expectedVersion + 1 }, draftId));
    }

    public Task<SaveResult> SetLifecycleAsync(
        Guid id,
        NoteLifecycle lifecycle,
        long expectedVersion,
        DateTimeOffset modifiedAtUtc,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public static SaveResult SavedResult(Note note, Guid? draftId) => new()
    {
        Status = SaveStatus.Saved,
        NoteId = note.Id,
        SavedNote = note,
        DraftId = draftId,
        DraftDisposition = DraftDisposition.DeletedAfterSave,
        Attempts = 1
    };

    internal sealed record SaveCall(
        bool IsCreate,
        Note Note,
        long ExpectedVersion,
        Guid? DraftId);
}

internal static class EditorTestData
{
    public static readonly DateTimeOffset Timestamp =
        new(2026, 10, 28, 9, 30, 0, TimeSpan.Zero);

    public static Note NewNote(long version = 1, string body = "body") => new()
    {
        Id = Guid.NewGuid(),
        Title = "title",
        Body = body,
        CreatedAtUtc = Timestamp,
        ModifiedAtUtc = Timestamp,
        Version = version,
        CategoryId = Category.UncategorizedId,
        DisplayIntent = NoteDisplayIntent.Visible
    };

    public static EditorAutosaveOptions DraftOnlyOptions() => new()
    {
        DraftDebounce = TimeSpan.Zero,
        DraftMaxDelay = TimeSpan.FromSeconds(1),
        SaveDebounce = TimeSpan.FromHours(1),
        SaveMaxDelay = TimeSpan.FromHours(2)
    };
}
