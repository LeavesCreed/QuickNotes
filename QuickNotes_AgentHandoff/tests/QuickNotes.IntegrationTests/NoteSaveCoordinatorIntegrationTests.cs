using Microsoft.Data.Sqlite;
using QuickNotes.Application;
using QuickNotes.Domain;
using QuickNotes.Infrastructure.Drafts;
using Xunit;

namespace QuickNotes.IntegrationTests;

public sealed class NoteSaveCoordinatorIntegrationTests
{
    [Fact]
    public async Task Create_WithMissingCategory_ReturnsValidationFailed()
    {
        using var database = await TestDatabase.CreateAsync();
        using var drafts = new TemporaryDirectory();
        var coordinator = CreateCoordinator(
            database.Notes,
            new FileNoteDraftStore(drafts.DraftDirectory));
        var note = TestData.NewNote() with { CategoryId = Guid.NewGuid() };

        var result = await coordinator.CreateAsync(note, Array.Empty<Guid>());

        Assert.Equal(SaveStatus.ValidationFailed, result.Status);
        Assert.Equal(SaveErrorCode.ValidationFailed, result.Error!.Code);
        Assert.Equal("constraint=foreign_key", result.Error.Diagnostics);
        Assert.Equal(1, result.Attempts);
        Assert.Null(await database.Notes.GetAsync(note.Id));
    }

    [Fact]
    public async Task Create_WithDuplicateId_ReturnsValidationFailedAsPrimaryKey()
    {
        using var database = await TestDatabase.CreateAsync();
        using var drafts = new TemporaryDirectory();
        var coordinator = CreateCoordinator(
            database.Notes,
            new FileNoteDraftStore(drafts.DraftDirectory));
        var note = TestData.NewNote();
        await coordinator.CreateAsync(note, Array.Empty<Guid>());

        var result = await coordinator.CreateAsync(note, Array.Empty<Guid>());

        Assert.Equal(SaveStatus.ValidationFailed, result.Status);
        Assert.Equal("constraint=primary_key", result.Error!.Diagnostics);
        Assert.Equal(1, result.Attempts);
    }

    [Fact]
    public async Task Update_WithInvalidTag_RollsBackAndReturnsValidationFailed()
    {
        using var database = await TestDatabase.CreateAsync();
        using var drafts = new TemporaryDirectory();
        var coordinator = CreateCoordinator(
            database.Notes,
            new FileNoteDraftStore(drafts.DraftDirectory));
        var note = TestData.NewNote();
        await coordinator.CreateAsync(note, Array.Empty<Guid>());

        var result = await coordinator.UpdateAsync(
            note with { Body = "must roll back", ModifiedAtUtc = note.ModifiedAtUtc.AddMinutes(1) },
            1,
            new[] { Guid.NewGuid() });

        Assert.Equal(SaveStatus.ValidationFailed, result.Status);
        Assert.Equal("constraint=foreign_key", result.Error!.Diagnostics);
        var loaded = await database.Notes.GetAsync(note.Id);
        Assert.Equal(note, loaded);
        Assert.Empty(await database.Notes.GetTagIdsAsync(note.Id));
    }

    [Fact]
    public async Task Update_WithStaleVersion_ReturnsConflictWithCurrentNote()
    {
        using var database = await TestDatabase.CreateAsync();
        using var drafts = new TemporaryDirectory();
        var coordinator = CreateCoordinator(
            database.Notes,
            new FileNoteDraftStore(drafts.DraftDirectory));
        var note = TestData.NewNote();
        await coordinator.CreateAsync(note, Array.Empty<Guid>());
        var newer = note with { Body = "newer", ModifiedAtUtc = note.ModifiedAtUtc.AddMinutes(1) };
        await coordinator.UpdateAsync(newer, 1, Array.Empty<Guid>());

        var result = await coordinator.UpdateAsync(
            note with { Body = "stale" },
            1,
            Array.Empty<Guid>());

        Assert.Equal(SaveStatus.Conflict, result.Status);
        Assert.Equal(SaveErrorCode.ConcurrencyConflict, result.Error!.Code);
        Assert.NotNull(result.CurrentNote);
        Assert.Equal(2, result.CurrentVersion);
        Assert.Equal("newer", result.CurrentNote!.Body);
        Assert.Equal(1, result.Attempts);
        Assert.Equal("newer", (await database.Notes.GetAsync(note.Id))!.Body);
    }

    [Fact]
    public async Task Update_WhenNoteMissing_ReturnsNotFound()
    {
        using var database = await TestDatabase.CreateAsync();
        using var drafts = new TemporaryDirectory();
        var coordinator = CreateCoordinator(
            database.Notes,
            new FileNoteDraftStore(drafts.DraftDirectory));

        var result = await coordinator.UpdateAsync(TestData.NewNote(), 1, Array.Empty<Guid>());

        Assert.Equal(SaveStatus.NotFound, result.Status);
        Assert.Equal(SaveErrorCode.NoteNotFound, result.Error!.Code);
    }

    [Fact]
    public async Task SetLifecycle_WhenNoteMissing_ReturnsNotFound()
    {
        using var database = await TestDatabase.CreateAsync();
        using var drafts = new TemporaryDirectory();
        var coordinator = CreateCoordinator(
            database.Notes,
            new FileNoteDraftStore(drafts.DraftDirectory));

        var result = await coordinator.SetLifecycleAsync(
            Guid.NewGuid(),
            NoteLifecycle.Archived,
            1,
            DateTimeOffset.UtcNow);

        Assert.Equal(SaveStatus.NotFound, result.Status);
        Assert.Equal(SaveErrorCode.NoteNotFound, result.Error!.Code);
    }

    [Fact]
    public async Task Update_ArchivedNoteWithVisibleIntent_ReturnsValidationFailed()
    {
        using var database = await TestDatabase.CreateAsync();
        using var drafts = new TemporaryDirectory();
        var coordinator = CreateCoordinator(
            database.Notes,
            new FileNoteDraftStore(drafts.DraftDirectory));
        var note = TestData.NewNote();
        await coordinator.CreateAsync(note, Array.Empty<Guid>());
        var archivedAt = note.ModifiedAtUtc.AddMinutes(1);
        await coordinator.SetLifecycleAsync(note.Id, NoteLifecycle.Archived, 1, archivedAt);

        var result = await coordinator.UpdateAsync(
            note with { Body = "visible on archived", ModifiedAtUtc = archivedAt.AddMinutes(1) },
            2,
            Array.Empty<Guid>());

        Assert.Equal(SaveStatus.ValidationFailed, result.Status);
        Assert.Equal("constraint=check", result.Error!.Diagnostics);
    }

    [Fact(Timeout = 15000)]
    public async Task Create_WhenLockReleasedBeforeRetry_SucceedsAfterBusy()
    {
        using var database = await TestDatabase.CreateAsync(busyTimeoutMilliseconds: 50);
        using var drafts = new TemporaryDirectory();
        var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = new SignalingNoteRepository(
            database.Notes,
            () => secondAttempt.TrySetResult());
        var coordinator = CreateCoordinator(
            repository,
            new FileNoteDraftStore(drafts.DraftDirectory),
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(100));
        await using var blocker = await database.Factory.OpenAsync();
        await ExecuteAsync(blocker, "BEGIN IMMEDIATE;");
        var note = TestData.NewNote();

        var saveTask = Task.Run(() => coordinator.CreateAsync(note, Array.Empty<Guid>()));
        await secondAttempt.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await ExecuteAsync(blocker, "ROLLBACK;");
        var result = await saveTask;

        Assert.Equal(SaveStatus.Saved, result.Status);
        Assert.Equal(2, result.Attempts);
        Assert.NotNull(await database.Notes.GetAsync(note.Id));
    }

    [Fact(Timeout = 15000)]
    public async Task Create_WhenLockRemainsHeld_ExhaustsRetriesAndRetainsDraft()
    {
        using var database = await TestDatabase.CreateAsync(busyTimeoutMilliseconds: 25);
        using var drafts = new TemporaryDirectory();
        var draftStore = new FileNoteDraftStore(drafts.DraftDirectory);
        var coordinator = CreateCoordinator(database.Notes, draftStore, TimeSpan.Zero, TimeSpan.Zero);
        var note = TestData.NewNote();
        var draftId = Guid.NewGuid();
        await draftStore.SaveAsync(NewDraft(draftId, note));
        await using var blocker = await database.Factory.OpenAsync();
        await ExecuteAsync(blocker, "BEGIN IMMEDIATE;");
        try
        {
            var result = await coordinator.CreateAsync(note, Array.Empty<Guid>(), draftId);

            Assert.Equal(SaveStatus.StorageUnavailable, result.Status);
            Assert.Equal(SaveErrorCode.StorageBusy, result.Error!.Code);
            Assert.True(result.Error.IsRetryable);
            Assert.Equal(3, result.Attempts);
            Assert.Equal(DraftDisposition.Retained, result.DraftDisposition);
            Assert.NotNull(await draftStore.GetAsync(draftId));
            Assert.Null(await database.Notes.GetAsync(note.Id));
        }
        finally
        {
            await ExecuteAsync(blocker, "ROLLBACK;");
        }
    }

    [Fact]
    public async Task Create_CommitUnknownAfterCommit_ReturnsSavedAndDeletesDraft()
    {
        using var database = await TestDatabase.CreateAsync();
        using var drafts = new TemporaryDirectory();
        var draftStore = new FileNoteDraftStore(drafts.DraftDirectory);
        var repository = new CommitUnknownNoteRepository(database.Notes, CommitUnknownMode.AfterCommit);
        var coordinator = CreateCoordinator(repository, draftStore);
        var note = TestData.NewNote();
        var draftId = Guid.NewGuid();
        await draftStore.SaveAsync(NewDraft(draftId, note));

        var result = await coordinator.CreateAsync(note, Array.Empty<Guid>(), draftId);

        Assert.Equal(SaveStatus.Saved, result.Status);
        Assert.NotNull(result.SavedNote);
        Assert.Equal(note.Id, result.SavedNote!.Id);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(DraftDisposition.DeletedAfterSave, result.DraftDisposition);
        Assert.NotNull(await database.Notes.GetAsync(note.Id));
        Assert.Null(await draftStore.GetAsync(draftId));
    }

    [Fact]
    public async Task Create_CommitUnknownBeforeWrite_ReturnsCommitFailedNotSaved()
    {
        using var database = await TestDatabase.CreateAsync();
        using var drafts = new TemporaryDirectory();
        var draftStore = new FileNoteDraftStore(drafts.DraftDirectory);
        var repository = new CommitUnknownNoteRepository(database.Notes, CommitUnknownMode.BeforeCommit);
        var coordinator = CreateCoordinator(repository, draftStore);
        var note = TestData.NewNote();
        var draftId = Guid.NewGuid();
        await draftStore.SaveAsync(NewDraft(draftId, note));

        var result = await coordinator.CreateAsync(note, Array.Empty<Guid>(), draftId);

        Assert.Equal(SaveStatus.StorageUnavailable, result.Status);
        Assert.Equal(SaveErrorCode.CommitFailedNotSaved, result.Error!.Code);
        Assert.True(result.Error.IsRetryable);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(DraftDisposition.Retained, result.DraftDisposition);
        Assert.Null(await database.Notes.GetAsync(note.Id));
        Assert.NotNull(await draftStore.GetAsync(draftId));
    }

    [Fact]
    public async Task Create_CommitUnknownWithChangedRow_ReturnsOutcomeUnknown()
    {
        using var database = await TestDatabase.CreateAsync();
        using var drafts = new TemporaryDirectory();
        var repository = new CommitUnknownNoteRepository(database.Notes, CommitUnknownMode.MutateAfterCommit);
        var coordinator = CreateCoordinator(repository, new FileNoteDraftStore(drafts.DraftDirectory));
        var note = TestData.NewNote();

        var result = await coordinator.CreateAsync(note, Array.Empty<Guid>());

        Assert.Equal(SaveStatus.OutcomeUnknown, result.Status);
        Assert.Equal(SaveErrorCode.CommitOutcomeUnknown, result.Error!.Code);
        Assert.False(result.Error.IsRetryable);
        Assert.NotNull(result.CurrentVersion);
        Assert.Equal(2, result.CurrentVersion);
    }

    [Fact]
    public async Task Create_SuccessWithMatchingDraft_DeletesDraftFile()
    {
        using var database = await TestDatabase.CreateAsync();
        using var drafts = new TemporaryDirectory();
        var draftStore = new FileNoteDraftStore(drafts.DraftDirectory);
        var coordinator = CreateCoordinator(database.Notes, draftStore);
        var tag = new Tag { Id = Guid.NewGuid(), Name = "draft-tag" };
        await database.Catalog.CreateTagAsync(tag);
        var note = TestData.NewNote();
        var draftId = Guid.NewGuid();
        await draftStore.SaveAsync(NewDraft(draftId, note, new[] { tag.Id }));

        var result = await coordinator.CreateAsync(note, new[] { tag.Id }, draftId);

        Assert.Equal(SaveStatus.Saved, result.Status);
        Assert.Equal(DraftDisposition.DeletedAfterSave, result.DraftDisposition);
        Assert.Null(await draftStore.GetAsync(draftId));
    }

    [Fact]
    public async Task Create_SuccessWithMismatchedDraft_RetainsDraftFile()
    {
        using var database = await TestDatabase.CreateAsync();
        using var drafts = new TemporaryDirectory();
        var draftStore = new FileNoteDraftStore(drafts.DraftDirectory);
        var coordinator = CreateCoordinator(database.Notes, draftStore);
        var note = TestData.NewNote();
        var draftId = Guid.NewGuid();
        await draftStore.SaveAsync(NewDraft(draftId, note) with { Body = "newer draft body" });

        var result = await coordinator.CreateAsync(note, Array.Empty<Guid>(), draftId);

        Assert.Equal(SaveStatus.Saved, result.Status);
        Assert.Equal(DraftDisposition.Retained, result.DraftDisposition);
        Assert.Equal(SaveErrorCode.DraftMismatch, result.DraftError!.Code);
        Assert.NotNull(await draftStore.GetAsync(draftId));
    }

    [Fact]
    public async Task Update_SuccessWithMatchingDraft_DeletesDraftFile()
    {
        using var database = await TestDatabase.CreateAsync();
        using var drafts = new TemporaryDirectory();
        var draftStore = new FileNoteDraftStore(drafts.DraftDirectory);
        var coordinator = CreateCoordinator(database.Notes, draftStore);
        var note = TestData.NewNote();
        await coordinator.CreateAsync(note, Array.Empty<Guid>());
        var updated = note with { Body = "updated body", ModifiedAtUtc = note.ModifiedAtUtc.AddMinutes(1) };
        var draftId = Guid.NewGuid();
        await draftStore.SaveAsync(NewDraft(draftId, updated));

        var result = await coordinator.UpdateAsync(updated, 1, Array.Empty<Guid>(), draftId);

        Assert.Equal(SaveStatus.Saved, result.Status);
        Assert.Equal(2, result.SavedNote!.Version);
        Assert.Equal(DraftDisposition.DeletedAfterSave, result.DraftDisposition);
        Assert.Null(await draftStore.GetAsync(draftId));
    }

    [Fact]
    public async Task SetLifecycle_Success_KeepsDraftFile()
    {
        using var database = await TestDatabase.CreateAsync();
        using var drafts = new TemporaryDirectory();
        var draftStore = new FileNoteDraftStore(drafts.DraftDirectory);
        var coordinator = CreateCoordinator(database.Notes, draftStore);
        var note = TestData.NewNote();
        await coordinator.CreateAsync(note, Array.Empty<Guid>());
        var draftId = Guid.NewGuid();
        await draftStore.SaveAsync(NewDraft(draftId, note));

        var result = await coordinator.SetLifecycleAsync(
            note.Id,
            NoteLifecycle.Archived,
            1,
            note.ModifiedAtUtc.AddMinutes(1));

        Assert.Equal(SaveStatus.Saved, result.Status);
        Assert.Equal(DraftDisposition.NotProvided, result.DraftDisposition);
        Assert.NotNull(await draftStore.GetAsync(draftId));
    }

    private static NoteSaveCoordinator CreateCoordinator(
        INoteRepository notes,
        INoteDraftStore drafts,
        params TimeSpan[] retryDelays)
    {
        var delays = retryDelays.Length == 0
            ? new[] { TimeSpan.Zero, TimeSpan.Zero }
            : retryDelays;
        return new NoteSaveCoordinator(
            notes,
            drafts,
            new SaveRetryOptions { RetryDelays = delays });
    }

    private static NoteDraft NewDraft(
        Guid draftId,
        Note note,
        IReadOnlyCollection<Guid>? tagIds = null) => new()
    {
        DraftId = draftId,
        NoteId = note.Id,
        BaseVersion = note.Version,
        Title = note.Title,
        Body = note.Body,
        CategoryId = note.CategoryId,
        TagIds = tagIds ?? Array.Empty<Guid>(),
        IsPinned = note.IsPinned,
        DisplayIntent = note.DisplayIntent,
        CapturedAtUtc = note.ModifiedAtUtc
    };

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}

internal sealed class SignalingNoteRepository : INoteRepository
{
    private readonly INoteRepository _inner;
    private readonly Action _onSecondCreate;
    private int _createCalls;

    public SignalingNoteRepository(INoteRepository inner, Action onSecondCreate)
    {
        _inner = inner;
        _onSecondCreate = onSecondCreate;
    }

    public Task<Note> CreateAsync(
        Note note,
        IReadOnlyCollection<Guid> tagIds,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref _createCalls) == 2)
        {
            _onSecondCreate();
        }

        return _inner.CreateAsync(note, tagIds, cancellationToken);
    }

    public Task<Note?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        _inner.GetAsync(id, cancellationToken);

    public Task<IReadOnlyList<Note>> ListAsync(
        NoteLifecycle lifecycle,
        CancellationToken cancellationToken = default) =>
        _inner.ListAsync(lifecycle, cancellationToken);

    public Task<IReadOnlyList<Guid>> GetTagIdsAsync(Guid id, CancellationToken cancellationToken = default) =>
        _inner.GetTagIdsAsync(id, cancellationToken);

    public Task<Note> UpdateAsync(
        Note note,
        long expectedVersion,
        IReadOnlyCollection<Guid> tagIds,
        CancellationToken cancellationToken = default) =>
        _inner.UpdateAsync(note, expectedVersion, tagIds, cancellationToken);

    public Task<Note> SetLifecycleAsync(
        Guid id,
        NoteLifecycle lifecycle,
        long expectedVersion,
        DateTimeOffset modifiedAtUtc,
        CancellationToken cancellationToken = default) =>
        _inner.SetLifecycleAsync(id, lifecycle, expectedVersion, modifiedAtUtc, cancellationToken);
}

internal enum CommitUnknownMode
{
    BeforeCommit,
    AfterCommit,
    MutateAfterCommit
}

internal sealed class CommitUnknownNoteRepository : INoteRepository
{
    private readonly INoteRepository _inner;
    private readonly CommitUnknownMode _mode;

    public CommitUnknownNoteRepository(INoteRepository inner, CommitUnknownMode mode)
    {
        _inner = inner;
        _mode = mode;
    }

    public Task<Note> CreateAsync(
        Note note,
        IReadOnlyCollection<Guid> tagIds,
        CancellationToken cancellationToken = default) =>
        CreateCoreAsync(note, tagIds, cancellationToken);

    public Task<Note?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        _inner.GetAsync(id, cancellationToken);

    public Task<IReadOnlyList<Note>> ListAsync(
        NoteLifecycle lifecycle,
        CancellationToken cancellationToken = default) =>
        _inner.ListAsync(lifecycle, cancellationToken);

    public Task<IReadOnlyList<Guid>> GetTagIdsAsync(Guid id, CancellationToken cancellationToken = default) =>
        _inner.GetTagIdsAsync(id, cancellationToken);

    public Task<Note> UpdateAsync(
        Note note,
        long expectedVersion,
        IReadOnlyCollection<Guid> tagIds,
        CancellationToken cancellationToken = default) =>
        _inner.UpdateAsync(note, expectedVersion, tagIds, cancellationToken);

    public Task<Note> SetLifecycleAsync(
        Guid id,
        NoteLifecycle lifecycle,
        long expectedVersion,
        DateTimeOffset modifiedAtUtc,
        CancellationToken cancellationToken = default) =>
        _inner.SetLifecycleAsync(id, lifecycle, expectedVersion, modifiedAtUtc, cancellationToken);

    private async Task<Note> CreateCoreAsync(
        Note note,
        IReadOnlyCollection<Guid> tagIds,
        CancellationToken cancellationToken)
    {
        if (_mode == CommitUnknownMode.BeforeCommit)
        {
            throw new SaveOutcomeUnknownException(
                "create",
                note.Id,
                0,
                new InvalidOperationException("simulated commit failure before write"));
        }

        var created = await _inner.CreateAsync(note, tagIds, cancellationToken);

        if (_mode == CommitUnknownMode.MutateAfterCommit)
        {
            await _inner.UpdateAsync(
                created with
                {
                    Body = "changed after commit",
                    ModifiedAtUtc = created.ModifiedAtUtc.AddMinutes(1)
                },
                created.Version,
                Array.Empty<Guid>(),
                cancellationToken);
        }

        throw new SaveOutcomeUnknownException(
            "create",
            note.Id,
            0,
            new InvalidOperationException("simulated commit failure after write"));
    }
}
