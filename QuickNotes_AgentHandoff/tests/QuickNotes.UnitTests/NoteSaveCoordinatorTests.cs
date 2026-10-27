using Microsoft.Extensions.Logging;
using QuickNotes.Application;
using QuickNotes.Domain;
using Xunit;

namespace QuickNotes.UnitTests;

public sealed class NoteSaveCoordinatorTests
{
    [Fact]
    public void Constructor_NullDependencies_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new NoteSaveCoordinator(null!, new FakeDraftStore()));
        Assert.Throws<ArgumentNullException>(() => new NoteSaveCoordinator(new FakeNoteRepository(), null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_InvalidMaxAttempts_Throws(int maxAttempts)
    {
        Assert.Throws<ArgumentException>(() => new NoteSaveCoordinator(
            new FakeNoteRepository(),
            new FakeDraftStore(),
            new SaveRetryOptions { MaxAttempts = maxAttempts }));
    }

    [Fact]
    public void Constructor_NullRetryDelays_Throws()
    {
        Assert.Throws<ArgumentException>(() => new NoteSaveCoordinator(
            new FakeNoteRepository(),
            new FakeDraftStore(),
            new SaveRetryOptions { RetryDelays = null! }));
    }

    [Fact]
    public void Constructor_TooFewRetryDelays_Throws()
    {
        Assert.Throws<ArgumentException>(() => new NoteSaveCoordinator(
            new FakeNoteRepository(),
            new FakeDraftStore(),
            new SaveRetryOptions
            {
                MaxAttempts = 3,
                RetryDelays = new[] { TimeSpan.Zero }
            }));
    }

    [Fact]
    public void Constructor_NegativeRetryDelay_Throws()
    {
        Assert.Throws<ArgumentException>(() => new NoteSaveCoordinator(
            new FakeNoteRepository(),
            new FakeDraftStore(),
            new SaveRetryOptions
            {
                RetryDelays = new[] { TimeSpan.FromMilliseconds(-1), TimeSpan.Zero }
            }));
    }

    [Fact]
    public async Task Constructor_ExtraRetryDelays_AreIgnored()
    {
        var coordinator = new NoteSaveCoordinator(
            new FakeNoteRepository(),
            new FakeDraftStore(),
            new SaveRetryOptions
            {
                MaxAttempts = 1,
                RetryDelays = new[] { TimeSpan.Zero, TimeSpan.Zero }
            });

        var result = await coordinator.CreateAsync(TestData.NewNote(), Array.Empty<Guid>());

        Assert.Equal(SaveStatus.Saved, result.Status);
    }

    [Fact]
    public async Task Create_Success_DeletesMatchingDraftAndReturnsSaved()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var note = TestData.NewNote();
        var draftId = Guid.NewGuid();
        var tagIds = new[] { Guid.NewGuid() };
        drafts.Drafts[draftId] = TestData.NewDraft(draftId, note, tagIds);
        var coordinator = CreateCoordinator(notes, drafts);

        var result = await coordinator.CreateAsync(note, tagIds, draftId);

        Assert.Equal(SaveStatus.Saved, result.Status);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(note.Id, result.NoteId);
        Assert.NotNull(result.SavedNote);
        Assert.Null(result.Error);
        Assert.Null(result.DraftError);
        Assert.Equal(draftId, result.DraftId);
        Assert.Equal(DraftDisposition.DeletedAfterSave, result.DraftDisposition);
        Assert.Equal(1, notes.CreateCalls);
        Assert.Equal(1, drafts.GetCalls);
        Assert.Equal(1, drafts.DeleteCalls);
        Assert.DoesNotContain(draftId, drafts.Drafts.Keys);
    }

    [Fact]
    public async Task Create_Success_UsesRepositoryRowAsSavedNote()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var note = TestData.NewNote();
        var stored = note with { Title = "stored title", Body = "stored body", Version = 9 };
        notes.CreateHandler = (_, _, _) => Task.FromResult(stored);
        var coordinator = CreateCoordinator(notes, drafts);

        var result = await coordinator.CreateAsync(note, Array.Empty<Guid>());

        Assert.Equal(SaveStatus.Saved, result.Status);
        Assert.Same(stored, result.SavedNote);
    }

    [Fact]
    public async Task Create_SuccessWithMismatchedBodyDraft_RetainsDraft()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var note = TestData.NewNote();
        var draftId = Guid.NewGuid();
        drafts.Drafts[draftId] = TestData.NewDraft(draftId, note) with { Body = "older draft" };
        var coordinator = CreateCoordinator(notes, drafts);

        var result = await coordinator.CreateAsync(note, Array.Empty<Guid>(), draftId);

        Assert.Equal(SaveStatus.Saved, result.Status);
        Assert.Equal(DraftDisposition.Retained, result.DraftDisposition);
        Assert.Equal(SaveErrorCode.DraftMismatch, result.DraftError!.Code);
        Assert.Equal(0, drafts.DeleteCalls);
        Assert.Contains(draftId, drafts.Drafts.Keys);
    }

    [Fact]
    public async Task Create_SuccessWithMismatchedNoteIdDraft_RetainsDraft()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var note = TestData.NewNote();
        var draftId = Guid.NewGuid();
        drafts.Drafts[draftId] = TestData.NewDraft(draftId, note) with { NoteId = Guid.NewGuid() };
        var coordinator = CreateCoordinator(notes, drafts);

        var result = await coordinator.CreateAsync(note, Array.Empty<Guid>(), draftId);

        Assert.Equal(SaveStatus.Saved, result.Status);
        Assert.Equal(DraftDisposition.Retained, result.DraftDisposition);
        Assert.Equal(SaveErrorCode.DraftMismatch, result.DraftError!.Code);
        Assert.Equal(0, drafts.DeleteCalls);
    }

    [Fact]
    public async Task Create_SuccessWithMismatchedTagsDraft_RetainsDraft()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var note = TestData.NewNote();
        var draftId = Guid.NewGuid();
        var savedTag = Guid.NewGuid();
        drafts.Drafts[draftId] = TestData.NewDraft(draftId, note, new[] { Guid.NewGuid() });
        var coordinator = CreateCoordinator(notes, drafts);

        var result = await coordinator.CreateAsync(note, new[] { savedTag }, draftId);

        Assert.Equal(SaveStatus.Saved, result.Status);
        Assert.Equal(DraftDisposition.Retained, result.DraftDisposition);
        Assert.Equal(SaveErrorCode.DraftMismatch, result.DraftError!.Code);
        Assert.Equal(0, drafts.DeleteCalls);
    }

    [Fact]
    public async Task Create_SuccessWithDraftReadFailure_ReportsCleanupFailed()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore { FailOnGet = true };
        var note = TestData.NewNote();
        var draftId = Guid.NewGuid();
        var coordinator = CreateCoordinator(notes, drafts);

        var result = await coordinator.CreateAsync(note, Array.Empty<Guid>(), draftId);

        Assert.Equal(SaveStatus.Saved, result.Status);
        Assert.Equal(DraftDisposition.CleanupFailed, result.DraftDisposition);
        Assert.Equal(SaveErrorCode.DraftCleanupFailed, result.DraftError!.Code);
        Assert.Equal(0, drafts.DeleteCalls);
    }

    [Fact]
    public async Task Create_SuccessWithDraftDeleteFailure_ReportsCleanupFailed()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore { FailOnDelete = true };
        var note = TestData.NewNote();
        var draftId = Guid.NewGuid();
        drafts.Drafts[draftId] = TestData.NewDraft(draftId, note);
        var coordinator = CreateCoordinator(notes, drafts);

        var result = await coordinator.CreateAsync(note, Array.Empty<Guid>(), draftId);

        Assert.Equal(SaveStatus.Saved, result.Status);
        Assert.Equal(DraftDisposition.CleanupFailed, result.DraftDisposition);
        Assert.Equal(SaveErrorCode.DraftCleanupFailed, result.DraftError!.Code);
        Assert.Equal(1, drafts.DeleteCalls);
        Assert.Contains(draftId, drafts.Drafts.Keys);
    }

    [Fact]
    public async Task Create_SuccessWithMissingDraft_ReportsAlreadyAbsent()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var draftId = Guid.NewGuid();
        var coordinator = CreateCoordinator(notes, drafts);

        var result = await coordinator.CreateAsync(TestData.NewNote(), Array.Empty<Guid>(), draftId);

        Assert.Equal(SaveStatus.Saved, result.Status);
        Assert.Equal(DraftDisposition.AlreadyAbsent, result.DraftDisposition);
        Assert.Null(result.DraftError);
        Assert.Equal(0, drafts.DeleteCalls);
    }

    [Fact]
    public async Task Create_SuccessWithoutDraft_DoesNotTouchDraftStore()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var coordinator = CreateCoordinator(notes, drafts);

        var result = await coordinator.CreateAsync(TestData.NewNote(), Array.Empty<Guid>());

        Assert.Equal(SaveStatus.Saved, result.Status);
        Assert.Equal(DraftDisposition.NotProvided, result.DraftDisposition);
        Assert.Null(result.DraftId);
        Assert.Equal(0, drafts.GetCalls);
        Assert.Equal(0, drafts.SaveCalls);
        Assert.Equal(0, drafts.DeleteCalls);
    }

    [Fact]
    public async Task Update_Conflict_ReturnsCurrentNoteAndRetainsDraft()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var note = TestData.NewNote();
        var current = note with { Body = "newer", Version = 3 };
        var draftId = Guid.NewGuid();
        drafts.Drafts[draftId] = TestData.NewDraft(draftId, note);
        notes.UpdateHandler = (_, _, _, _) =>
            throw new NoteConcurrencyException(note.Id, 1, 3, "update");
        notes.GetHandler = (_, _) => Task.FromResult<Note?>(current);
        var coordinator = CreateCoordinator(notes, drafts);

        var result = await coordinator.UpdateAsync(note, 1, Array.Empty<Guid>(), draftId);

        Assert.Equal(SaveStatus.Conflict, result.Status);
        Assert.Equal(SaveErrorCode.ConcurrencyConflict, result.Error!.Code);
        Assert.False(result.Error.IsRetryable);
        Assert.Same(current, result.CurrentNote);
        Assert.Equal(3, result.CurrentVersion);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(DraftDisposition.Retained, result.DraftDisposition);
        Assert.Equal(1, notes.UpdateCalls);
        Assert.Equal(0, drafts.DeleteCalls);
    }

    [Fact]
    public async Task Update_ConflictWithReadbackFailure_StillReturnsConflict()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var note = TestData.NewNote();
        notes.UpdateHandler = (_, _, _, _) =>
            throw new NoteConcurrencyException(note.Id, 1, 3, "update");
        notes.GetHandler = (_, _) =>
            throw new StorageUnavailableException("get", note.Id, "sqlite_io", false, "read failed");
        var coordinator = CreateCoordinator(notes, drafts);

        var result = await coordinator.UpdateAsync(note, 1, Array.Empty<Guid>());

        Assert.Equal(SaveStatus.Conflict, result.Status);
        Assert.Null(result.CurrentNote);
        Assert.Equal(3, result.CurrentVersion);
        Assert.Equal(SaveErrorCode.ConcurrencyConflict, result.Error!.Code);
    }

    [Fact]
    public async Task Update_WhenNoteMissing_ReturnsNotFoundWithDraftRetained()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var note = TestData.NewNote();
        var draftId = Guid.NewGuid();
        drafts.Drafts[draftId] = TestData.NewDraft(draftId, note);
        notes.UpdateHandler = (_, _, _, _) => throw new NoteNotFoundException(note.Id, "update");
        var coordinator = CreateCoordinator(notes, drafts);

        var result = await coordinator.UpdateAsync(note, 1, Array.Empty<Guid>(), draftId);

        Assert.Equal(SaveStatus.NotFound, result.Status);
        Assert.Equal(SaveErrorCode.NoteNotFound, result.Error!.Code);
        Assert.Null(result.SavedNote);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(DraftDisposition.Retained, result.DraftDisposition);
        Assert.Equal(0, drafts.DeleteCalls);
    }

    [Fact]
    public async Task Create_ConstraintViolation_ReturnsValidationFailedWithoutRetry()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var note = TestData.NewNote();
        notes.CreateHandler = (_, _, _) =>
            throw new NoteConstraintException("create", note.Id, "foreign_key", "constraint violation");
        var coordinator = CreateCoordinator(notes, drafts);

        var result = await coordinator.CreateAsync(note, Array.Empty<Guid>());

        Assert.Equal(SaveStatus.ValidationFailed, result.Status);
        Assert.Equal(SaveErrorCode.ValidationFailed, result.Error!.Code);
        Assert.Equal("constraint=foreign_key", result.Error.Diagnostics);
        Assert.False(result.Error.IsRetryable);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, notes.CreateCalls);
    }

    [Fact]
    public async Task Create_NonRetryableStorageError_ReturnsStorageUnavailableWithoutRetry()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var note = TestData.NewNote();
        var draftId = Guid.NewGuid();
        drafts.Drafts[draftId] = TestData.NewDraft(draftId, note);
        notes.CreateHandler = (_, _, _) =>
            throw new StorageUnavailableException("create", note.Id, "sqlite_io", false, "io failure");
        var coordinator = CreateCoordinator(notes, drafts);

        var result = await coordinator.CreateAsync(note, Array.Empty<Guid>(), draftId);

        Assert.Equal(SaveStatus.StorageUnavailable, result.Status);
        Assert.Equal(SaveErrorCode.StorageUnavailable, result.Error!.Code);
        Assert.False(result.Error.IsRetryable);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, notes.CreateCalls);
        Assert.Equal(DraftDisposition.Retained, result.DraftDisposition);
        Assert.Equal(0, drafts.GetCalls);
        Assert.Equal(0, drafts.DeleteCalls);
    }

    [Fact]
    public async Task Create_BusyThenConstraint_CountsRetriesAndRetainsDraft()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var note = TestData.NewNote();
        var draftId = Guid.NewGuid();
        drafts.Drafts[draftId] = TestData.NewDraft(draftId, note);
        var calls = 0;
        notes.CreateHandler = (input, _, _) =>
        {
            calls++;
            if (calls == 1)
            {
                throw new StorageUnavailableException("create", input.Id, "sqlite_busy", true, "busy");
            }

            throw new NoteConstraintException("create", input.Id, "foreign_key", "constraint");
        };
        var coordinator = CreateCoordinator(notes, drafts);

        var result = await coordinator.CreateAsync(note, Array.Empty<Guid>(), draftId);

        Assert.Equal(SaveStatus.ValidationFailed, result.Status);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, notes.CreateCalls);
        Assert.Equal(DraftDisposition.Retained, result.DraftDisposition);
        Assert.Equal(0, drafts.DeleteCalls);
    }

    [Fact]
    public async Task Update_BusyThenConflict_CountsRetriesAndReturnsConflict()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var note = TestData.NewNote();
        var current = note with { Version = 2 };
        var calls = 0;
        notes.UpdateHandler = (_, _, _, _) =>
        {
            calls++;
            if (calls == 1)
            {
                throw new StorageUnavailableException("update", note.Id, "sqlite_busy", true, "busy");
            }

            throw new NoteConcurrencyException(note.Id, 1, 2, "update");
        };
        notes.GetHandler = (_, _) => Task.FromResult<Note?>(current);
        var coordinator = CreateCoordinator(notes, drafts);

        var result = await coordinator.UpdateAsync(note, 1, Array.Empty<Guid>());

        Assert.Equal(SaveStatus.Conflict, result.Status);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, notes.UpdateCalls);
        Assert.Same(current, result.CurrentNote);
    }

    [Fact]
    public async Task Create_ValidationFailureWithDraft_RetainsDraftWithoutReadingIt()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var note = TestData.NewNote();
        var draftId = Guid.NewGuid();
        drafts.Drafts[draftId] = TestData.NewDraft(draftId, note);
        var coordinator = CreateCoordinator(notes, drafts);

        var result = await coordinator.CreateAsync(
            note with { Id = Guid.Empty },
            Array.Empty<Guid>(),
            draftId);

        Assert.Equal(SaveStatus.ValidationFailed, result.Status);
        Assert.Equal(DraftDisposition.Retained, result.DraftDisposition);
        Assert.Equal(0, notes.CreateCalls);
        Assert.Equal(0, drafts.GetCalls);
        Assert.Equal(0, drafts.DeleteCalls);
        Assert.Contains(draftId, drafts.Drafts.Keys);
    }

    [Fact]
    public async Task Create_BusyTwiceThenSuccess_RetriesAndReportsThreeAttempts()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var note = TestData.NewNote();
        var calls = 0;
        notes.CreateHandler = (input, _, _) =>
        {
            calls++;
            return calls < 3
                ? throw new StorageUnavailableException("create", input.Id, "sqlite_busy", true, "busy")
                : Task.FromResult(input with { Version = 1 });
        };
        var coordinator = CreateCoordinator(notes, drafts);

        var result = await coordinator.CreateAsync(note, Array.Empty<Guid>());

        Assert.Equal(SaveStatus.Saved, result.Status);
        Assert.Equal(3, result.Attempts);
        Assert.Equal(3, notes.CreateCalls);
    }

    [Fact]
    public async Task Create_BusyExhausted_ReturnsStorageBusyWithRetryableError()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var note = TestData.NewNote();
        var draftId = Guid.NewGuid();
        notes.CreateHandler = (_, _, _) =>
            throw new StorageUnavailableException("create", note.Id, "sqlite_busy", true, "busy");
        var coordinator = CreateCoordinator(notes, drafts);

        var result = await coordinator.CreateAsync(note, Array.Empty<Guid>(), draftId);

        Assert.Equal(SaveStatus.StorageUnavailable, result.Status);
        Assert.Equal(SaveErrorCode.StorageBusy, result.Error!.Code);
        Assert.True(result.Error.IsRetryable);
        Assert.Equal(3, result.Attempts);
        Assert.Equal(3, notes.CreateCalls);
        Assert.Equal(DraftDisposition.Retained, result.DraftDisposition);
        Assert.Equal(0, drafts.GetCalls);
    }

    [Fact]
    public async Task Create_LockedExhausted_ReturnsStorageLockedWithRetryableError()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var note = TestData.NewNote();
        notes.CreateHandler = (_, _, _) =>
            throw new StorageUnavailableException("create", note.Id, "sqlite_locked", true, "locked");
        var coordinator = CreateCoordinator(notes, drafts);

        var result = await coordinator.CreateAsync(note, Array.Empty<Guid>());

        Assert.Equal(SaveStatus.StorageUnavailable, result.Status);
        Assert.Equal(SaveErrorCode.StorageLocked, result.Error!.Code);
        Assert.True(result.Error.IsRetryable);
        Assert.Equal(3, result.Attempts);
    }

    [Fact]
    public async Task Create_CommitUnknownWithMatchingReadback_ReturnsSavedAndCleansDraft()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var note = TestData.NewNote();
        var tagIds = new[] { Guid.NewGuid() };
        var draftId = Guid.NewGuid();
        drafts.Drafts[draftId] = TestData.NewDraft(draftId, note, tagIds);
        var stored = note with { Version = 1 };
        notes.CreateHandler = (_, _, _) =>
            throw new SaveOutcomeUnknownException("create", note.Id, 0, new InvalidOperationException("commit failed"));
        notes.GetHandler = (_, _) => Task.FromResult<Note?>(stored);
        notes.GetTagIdsHandler = (_, _) => Task.FromResult<IReadOnlyList<Guid>>(tagIds);
        var coordinator = CreateCoordinator(notes, drafts);

        var result = await coordinator.CreateAsync(note, tagIds, draftId);

        Assert.Equal(SaveStatus.Saved, result.Status);
        Assert.Same(stored, result.SavedNote);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, notes.CreateCalls);
        Assert.Equal(DraftDisposition.DeletedAfterSave, result.DraftDisposition);
    }

    [Fact]
    public async Task Create_CommitUnknownWithNoReadbackRow_ReturnsCommitFailedNotSaved()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var note = TestData.NewNote();
        var draftId = Guid.NewGuid();
        drafts.Drafts[draftId] = TestData.NewDraft(draftId, note);
        notes.CreateHandler = (_, _, _) =>
            throw new SaveOutcomeUnknownException("create", note.Id, 0, new InvalidOperationException("commit failed"));
        var coordinator = CreateCoordinator(notes, drafts);

        var result = await coordinator.CreateAsync(note, Array.Empty<Guid>(), draftId);

        Assert.Equal(SaveStatus.StorageUnavailable, result.Status);
        Assert.Equal(SaveErrorCode.CommitFailedNotSaved, result.Error!.Code);
        Assert.True(result.Error.IsRetryable);
        Assert.Null(result.SavedNote);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, notes.CreateCalls);
        Assert.Equal(DraftDisposition.Retained, result.DraftDisposition);
        Assert.Equal(0, drafts.DeleteCalls);
    }

    [Fact]
    public async Task Create_CommitUnknownWithInconsistentReadback_ReturnsOutcomeUnknown()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var note = TestData.NewNote();
        var stored = note with { Version = 2, Body = "changed by another writer" };
        notes.CreateHandler = (_, _, _) =>
            throw new SaveOutcomeUnknownException("create", note.Id, 0, new InvalidOperationException("commit failed"));
        notes.GetHandler = (_, _) => Task.FromResult<Note?>(stored);
        var coordinator = CreateCoordinator(notes, drafts);

        var result = await coordinator.CreateAsync(note, Array.Empty<Guid>());

        Assert.Equal(SaveStatus.OutcomeUnknown, result.Status);
        Assert.Equal(SaveErrorCode.CommitOutcomeUnknown, result.Error!.Code);
        Assert.False(result.Error.IsRetryable);
        Assert.Same(stored, result.CurrentNote);
        Assert.Equal(2, result.CurrentVersion);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, notes.CreateCalls);
    }

    [Fact]
    public async Task Create_CommitUnknownWithReadFailure_ReturnsOutcomeUnknown()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var note = TestData.NewNote();
        notes.CreateHandler = (_, _, _) =>
            throw new SaveOutcomeUnknownException("create", note.Id, 0, new InvalidOperationException("commit failed"));
        notes.GetHandler = (_, _) => throw new InvalidOperationException("read failed");
        var coordinator = CreateCoordinator(notes, drafts);

        var result = await coordinator.CreateAsync(note, Array.Empty<Guid>());

        Assert.Equal(SaveStatus.OutcomeUnknown, result.Status);
        Assert.Equal(SaveErrorCode.CommitOutcomeUnknown, result.Error!.Code);
        Assert.Contains("InvalidOperationException", result.Error.Diagnostics);
        Assert.Null(result.CurrentNote);
        Assert.Equal(1, result.Attempts);
    }

    [Fact]
    public async Task Update_CommitUnknownWithTagReadFailure_FillsCurrentNote()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var note = TestData.NewNote();
        var stored = note with { Version = 2, Body = "updated" };
        notes.UpdateHandler = (_, _, _, _) =>
            throw new SaveOutcomeUnknownException("update", note.Id, 1, new InvalidOperationException("commit failed"));
        notes.GetHandler = (_, _) => Task.FromResult<Note?>(stored);
        notes.GetTagIdsHandler = (_, _) => throw new InvalidOperationException("tag read failed");
        var coordinator = CreateCoordinator(notes, drafts);

        var result = await coordinator.UpdateAsync(note with { Body = "updated" }, 1, Array.Empty<Guid>());

        Assert.Equal(SaveStatus.OutcomeUnknown, result.Status);
        Assert.Same(stored, result.CurrentNote);
        Assert.Equal(2, result.CurrentVersion);
        Assert.Contains("InvalidOperationException", result.Error!.Diagnostics);
    }

    [Fact]
    public async Task Create_CommitUnknownWithRollbackFailure_ExposesRollbackDiagnostic()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var note = TestData.NewNote();
        var commitException = new SaveOutcomeUnknownException(
            "create",
            note.Id,
            0,
            new InvalidOperationException("commit failed"));
        commitException.Data["rollback_error"] = "SqliteException:10";
        notes.CreateHandler = (_, _, _) => throw commitException;
        var coordinator = CreateCoordinator(notes, drafts);

        var result = await coordinator.CreateAsync(note, Array.Empty<Guid>());

        Assert.Equal(SaveStatus.StorageUnavailable, result.Status);
        Assert.Contains("rollback_error=SqliteException:10", result.Error!.Diagnostics);
    }

    [Fact]
    public async Task Update_CommitUnknownWithMatchingReadback_ReturnsSaved()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var note = TestData.NewNote();
        var modified = note.ModifiedAtUtc.AddMinutes(1);
        var input = note with { Body = "updated", ModifiedAtUtc = modified };
        var stored = input with { Version = 2 };
        notes.UpdateHandler = (_, _, _, _) =>
            throw new SaveOutcomeUnknownException("update", note.Id, 1, new InvalidOperationException("commit failed"));
        notes.GetHandler = (_, _) => Task.FromResult<Note?>(stored);
        var coordinator = CreateCoordinator(notes, drafts);

        var result = await coordinator.UpdateAsync(input, 1, Array.Empty<Guid>());

        Assert.Equal(SaveStatus.Saved, result.Status);
        Assert.Same(stored, result.SavedNote);
        Assert.Equal(1, notes.UpdateCalls);
    }

    [Fact]
    public async Task Update_CommitUnknownWithExpectedVersionReadback_ReturnsCommitFailedNotSaved()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var note = TestData.NewNote();
        var input = note with { Body = "updated", ModifiedAtUtc = note.ModifiedAtUtc.AddMinutes(1) };
        notes.UpdateHandler = (_, _, _, _) =>
            throw new SaveOutcomeUnknownException("update", note.Id, 1, new InvalidOperationException("commit failed"));
        notes.GetHandler = (_, _) => Task.FromResult<Note?>(note with { Version = 1 });
        var coordinator = CreateCoordinator(notes, drafts);

        var result = await coordinator.UpdateAsync(input, 1, Array.Empty<Guid>());

        Assert.Equal(SaveStatus.StorageUnavailable, result.Status);
        Assert.Equal(SaveErrorCode.CommitFailedNotSaved, result.Error!.Code);
        Assert.True(result.Error.IsRetryable);
        Assert.Equal(1, notes.UpdateCalls);
    }

    [Fact]
    public async Task Update_CommitUnknownWithVersionGap_ReturnsOutcomeUnknown()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var note = TestData.NewNote();
        var input = note with { Body = "updated", ModifiedAtUtc = note.ModifiedAtUtc.AddMinutes(1) };
        var stored = input with { Version = 3 };
        notes.UpdateHandler = (_, _, _, _) =>
            throw new SaveOutcomeUnknownException("update", note.Id, 1, new InvalidOperationException("commit failed"));
        notes.GetHandler = (_, _) => Task.FromResult<Note?>(stored);
        var coordinator = CreateCoordinator(notes, drafts);

        var result = await coordinator.UpdateAsync(input, 1, Array.Empty<Guid>());

        Assert.Equal(SaveStatus.OutcomeUnknown, result.Status);
        Assert.Equal(SaveErrorCode.CommitOutcomeUnknown, result.Error!.Code);
        Assert.Equal(3, result.CurrentVersion);
    }

    [Fact]
    public async Task Update_CommitUnknownWithMissingRow_ReturnsNotFound()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var note = TestData.NewNote();
        notes.UpdateHandler = (_, _, _, _) =>
            throw new SaveOutcomeUnknownException("update", note.Id, 1, new InvalidOperationException("commit failed"));
        var coordinator = CreateCoordinator(notes, drafts);

        var result = await coordinator.UpdateAsync(note, 1, Array.Empty<Guid>());

        Assert.Equal(SaveStatus.NotFound, result.Status);
        Assert.Equal(SaveErrorCode.NoteNotFound, result.Error!.Code);
    }

    [Fact]
    public async Task SetLifecycle_Success_DoesNotTouchDraftStore()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var id = Guid.NewGuid();
        var modified = TestData.Timestamp.AddHours(1);
        var coordinator = CreateCoordinator(notes, drafts);

        var result = await coordinator.SetLifecycleAsync(id, NoteLifecycle.Archived, 1, modified);

        Assert.Equal(SaveStatus.Saved, result.Status);
        Assert.Equal(id, result.NoteId);
        Assert.Equal(DraftDisposition.NotProvided, result.DraftDisposition);
        Assert.Null(result.DraftId);
        Assert.Equal(1, notes.SetLifecycleCalls);
        Assert.Equal(0, drafts.GetCalls);
        Assert.Equal(0, drafts.ListCalls);
        Assert.Equal(0, drafts.DeleteCalls);
    }

    [Fact]
    public async Task SetLifecycle_CommitUnknownWithMatchingReadback_ReturnsSaved()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var id = Guid.NewGuid();
        var modified = TestData.Timestamp.AddHours(1);
        var stored = new Note
        {
            Id = id,
            Title = null,
            Body = "stored",
            CreatedAtUtc = TestData.Timestamp,
            ModifiedAtUtc = modified,
            Version = 2,
            Lifecycle = NoteLifecycle.Archived,
            CategoryId = Category.UncategorizedId,
            DisplayIntent = NoteDisplayIntent.Hidden
        };
        notes.SetLifecycleHandler = (_, _, _, _, _) =>
            throw new SaveOutcomeUnknownException("set_lifecycle", id, 1, new InvalidOperationException("commit failed"));
        notes.GetHandler = (_, _) => Task.FromResult<Note?>(stored);
        var coordinator = CreateCoordinator(notes, drafts);

        var result = await coordinator.SetLifecycleAsync(id, NoteLifecycle.Archived, 1, modified);

        Assert.Equal(SaveStatus.Saved, result.Status);
        Assert.Same(stored, result.SavedNote);
        Assert.Equal(DraftDisposition.NotProvided, result.DraftDisposition);
    }

    [Fact]
    public async Task SetLifecycle_CommitUnknownWithExpectedVersionReadback_ReturnsCommitFailedNotSaved()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var id = Guid.NewGuid();
        var modified = TestData.Timestamp.AddHours(1);
        notes.SetLifecycleHandler = (_, _, _, _, _) =>
            throw new SaveOutcomeUnknownException("set_lifecycle", id, 1, new InvalidOperationException("commit failed"));
        notes.GetHandler = (_, _) => Task.FromResult<Note?>(new Note
        {
            Id = id,
            Body = "stored",
            CreatedAtUtc = TestData.Timestamp,
            ModifiedAtUtc = TestData.Timestamp,
            Version = 1,
            Lifecycle = NoteLifecycle.Active,
            CategoryId = Category.UncategorizedId
        });
        var coordinator = CreateCoordinator(notes, drafts);

        var result = await coordinator.SetLifecycleAsync(id, NoteLifecycle.Archived, 1, modified);

        Assert.Equal(SaveStatus.StorageUnavailable, result.Status);
        Assert.Equal(SaveErrorCode.CommitFailedNotSaved, result.Error!.Code);
    }

    [Fact]
    public async Task SetLifecycle_CommitUnknownWithStateMismatch_ReturnsOutcomeUnknown()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var id = Guid.NewGuid();
        var modified = TestData.Timestamp.AddHours(1);
        notes.SetLifecycleHandler = (_, _, _, _, _) =>
            throw new SaveOutcomeUnknownException("set_lifecycle", id, 1, new InvalidOperationException("commit failed"));
        notes.GetHandler = (_, _) => Task.FromResult<Note?>(new Note
        {
            Id = id,
            Body = "stored",
            CreatedAtUtc = TestData.Timestamp,
            ModifiedAtUtc = modified,
            Version = 2,
            Lifecycle = NoteLifecycle.Active,
            CategoryId = Category.UncategorizedId
        });
        var coordinator = CreateCoordinator(notes, drafts);

        var result = await coordinator.SetLifecycleAsync(id, NoteLifecycle.Archived, 1, modified);

        Assert.Equal(SaveStatus.OutcomeUnknown, result.Status);
        Assert.Equal(SaveErrorCode.CommitOutcomeUnknown, result.Error!.Code);
    }

    [Fact]
    public async Task Create_InvalidInput_ReturnsValidationFailedWithoutTouchingStorage()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var coordinator = CreateCoordinator(notes, drafts);
        var note = TestData.NewNote();
        var tagId = Guid.NewGuid();

        var cases = new (string Reason, Func<Task<SaveResult>> Call)[]
        {
            ("note_null", () => coordinator.CreateAsync(null!, Array.Empty<Guid>())),
            ("note_id_empty", () => coordinator.CreateAsync(note with { Id = Guid.Empty }, Array.Empty<Guid>())),
            ("category_id_empty", () => coordinator.CreateAsync(note with { CategoryId = Guid.Empty }, Array.Empty<Guid>())),
            ("body_null", () => coordinator.CreateAsync(note with { Body = null! }, Array.Empty<Guid>())),
            ("display_intent_undefined", () => coordinator.CreateAsync(note with { DisplayIntent = (NoteDisplayIntent)42 }, Array.Empty<Guid>())),
            ("tag_ids_null", () => coordinator.CreateAsync(note, null!)),
            ("tag_id_empty", () => coordinator.CreateAsync(note, new[] { tagId, Guid.Empty })),
            ("draft_id_empty", () => coordinator.CreateAsync(note, Array.Empty<Guid>(), Guid.Empty)),
            ("lifecycle_must_be_active", () => coordinator.CreateAsync(note with { Lifecycle = NoteLifecycle.Archived }, Array.Empty<Guid>()))
        };

        foreach (var (reason, call) in cases)
        {
            var result = await call();

            Assert.Equal(SaveStatus.ValidationFailed, result.Status);
            Assert.Equal(SaveErrorCode.ValidationFailed, result.Error!.Code);
            Assert.Equal(reason, result.Error.Diagnostics);
            Assert.Equal(0, result.Attempts);
            Assert.Null(result.SavedNote);
        }

        Assert.Equal(0, notes.CreateCalls);
        Assert.Equal(0, drafts.GetCalls);
        Assert.Equal(0, drafts.SaveCalls);
        Assert.Equal(0, drafts.DeleteCalls);
    }

    [Fact]
    public async Task Update_InvalidInput_ReturnsValidationFailedWithoutTouchingStorage()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var coordinator = CreateCoordinator(notes, drafts);
        var note = TestData.NewNote();

        var cases = new (string Reason, Func<Task<SaveResult>> Call)[]
        {
            ("note_null", () => coordinator.UpdateAsync(null!, 1, Array.Empty<Guid>())),
            ("note_id_empty", () => coordinator.UpdateAsync(note with { Id = Guid.Empty }, 1, Array.Empty<Guid>())),
            ("category_id_empty", () => coordinator.UpdateAsync(note with { CategoryId = Guid.Empty }, 1, Array.Empty<Guid>())),
            ("body_null", () => coordinator.UpdateAsync(note with { Body = null! }, 1, Array.Empty<Guid>())),
            ("display_intent_undefined", () => coordinator.UpdateAsync(note with { DisplayIntent = (NoteDisplayIntent)42 }, 1, Array.Empty<Guid>())),
            ("tag_ids_null", () => coordinator.UpdateAsync(note, 1, null!)),
            ("tag_id_empty", () => coordinator.UpdateAsync(note, 1, new[] { Guid.Empty })),
            ("draft_id_empty", () => coordinator.UpdateAsync(note, 1, Array.Empty<Guid>(), Guid.Empty)),
            ("expected_version_invalid", () => coordinator.UpdateAsync(note, 0, Array.Empty<Guid>()))
        };

        foreach (var (reason, call) in cases)
        {
            var result = await call();

            Assert.Equal(SaveStatus.ValidationFailed, result.Status);
            Assert.Equal(reason, result.Error!.Diagnostics);
            Assert.Equal(0, result.Attempts);
        }

        Assert.Equal(0, notes.UpdateCalls);
        Assert.Equal(0, drafts.GetCalls);
    }

    [Fact]
    public async Task SetLifecycle_InvalidInput_ReturnsValidationFailedWithoutTouchingStorage()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var coordinator = CreateCoordinator(notes, drafts);

        var cases = new (string Reason, Func<Task<SaveResult>> Call)[]
        {
            ("note_id_empty", () => coordinator.SetLifecycleAsync(Guid.Empty, NoteLifecycle.Archived, 1, TestData.Timestamp)),
            ("lifecycle_undefined", () => coordinator.SetLifecycleAsync(Guid.NewGuid(), (NoteLifecycle)42, 1, TestData.Timestamp)),
            ("expected_version_invalid", () => coordinator.SetLifecycleAsync(Guid.NewGuid(), NoteLifecycle.Archived, 0, TestData.Timestamp))
        };

        foreach (var (reason, call) in cases)
        {
            var result = await call();

            Assert.Equal(SaveStatus.ValidationFailed, result.Status);
            Assert.Equal(reason, result.Error!.Diagnostics);
            Assert.Equal(0, result.Attempts);
        }

        Assert.Equal(0, notes.SetLifecycleCalls);
    }

    [Fact]
    public async Task Create_DuplicateTags_PassesValidationAndForwardsCollection()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var coordinator = CreateCoordinator(notes, drafts);
        var note = TestData.NewNote();
        var tagId = Guid.NewGuid();
        var tagIds = new[] { tagId, tagId };

        var result = await coordinator.CreateAsync(note, tagIds);

        Assert.Equal(SaveStatus.Saved, result.Status);
        Assert.Same(tagIds, notes.LastCreateTagIds);
    }

    [Fact]
    public async Task Create_CancelledBeforeFirstAttempt_ThrowsOperationCanceled()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var coordinator = CreateCoordinator(notes, drafts);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.CreateAsync(TestData.NewNote(), Array.Empty<Guid>(), null, cancellation.Token));

        Assert.Equal(0, notes.CreateCalls);
    }

    [Fact]
    public async Task Create_CancelledDuringRetryDelay_ThrowsOperationCanceled()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var note = TestData.NewNote();
        using var cancellation = new CancellationTokenSource();
        notes.CreateHandler = (input, _, _) =>
        {
            cancellation.Cancel();
            throw new StorageUnavailableException("create", input.Id, "sqlite_busy", true, "busy");
        };
        var coordinator = CreateCoordinator(notes, drafts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.CreateAsync(note, Array.Empty<Guid>(), null, cancellation.Token));

        Assert.Equal(1, notes.CreateCalls);
    }

    [Fact]
    public async Task Create_UnexpectedException_IsRethrown()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var note = TestData.NewNote();
        notes.CreateHandler = (_, _, _) => throw new InvalidOperationException("programming error");
        var coordinator = CreateCoordinator(notes, drafts);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.CreateAsync(note, Array.Empty<Guid>()));

        Assert.Equal("programming error", exception.Message);
        Assert.Equal(1, notes.CreateCalls);
    }

    [Fact]
    public async Task Logs_DoNotContainNoteContent()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var logger = new CapturingLogger<NoteSaveCoordinator>();
        var note = TestData.NewNote() with { Title = "TOPSECRET_TITLE", Body = "TOPSECRET_BODY" };
        var tagId = Guid.NewGuid();
        notes.CreateHandler = (_, _, _) =>
            throw new NoteConstraintException("create", note.Id, "check", "constraint violation");
        var coordinator = CreateCoordinator(notes, drafts, logger);

        await coordinator.CreateAsync(note, new[] { tagId });

        var messages = string.Join(Environment.NewLine, logger.Entries.Select(entry => entry.Message));
        Assert.DoesNotContain("TOPSECRET_TITLE", messages);
        Assert.DoesNotContain("TOPSECRET_BODY", messages);
        Assert.DoesNotContain(tagId.ToString(), messages);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Create_Success_LogsInformationWithoutContent()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var logger = new CapturingLogger<NoteSaveCoordinator>();
        var note = TestData.NewNote() with { Title = "TOPSECRET_TITLE", Body = "TOPSECRET_BODY" };
        var coordinator = CreateCoordinator(notes, drafts, logger);

        await coordinator.CreateAsync(note, Array.Empty<Guid>());

        var messages = string.Join(Environment.NewLine, logger.Entries.Select(entry => entry.Message));
        Assert.DoesNotContain("TOPSECRET_TITLE", messages);
        Assert.DoesNotContain("TOPSECRET_BODY", messages);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Information);
    }

    private static NoteSaveCoordinator CreateCoordinator(
        FakeNoteRepository notes,
        FakeDraftStore drafts,
        ILogger<NoteSaveCoordinator>? logger = null,
        SaveRetryOptions? retryOptions = null)
    {
        return new NoteSaveCoordinator(
            notes,
            drafts,
            retryOptions ?? new SaveRetryOptions
            {
                RetryDelays = new[] { TimeSpan.Zero, TimeSpan.Zero }
            },
            logger);
    }
}
