using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QuickNotes.Domain;

namespace QuickNotes.Application;

public sealed class NoteSaveCoordinator : INoteSaveCoordinator
{
    private readonly INoteRepository _notes;
    private readonly INoteDraftStore _drafts;
    private readonly SaveRetryOptions _retryOptions;
    private readonly ILogger<NoteSaveCoordinator> _logger;

    public NoteSaveCoordinator(
        INoteRepository notes,
        INoteDraftStore drafts,
        SaveRetryOptions? retryOptions = null,
        ILogger<NoteSaveCoordinator>? logger = null)
    {
        _notes = notes ?? throw new ArgumentNullException(nameof(notes));
        _drafts = drafts ?? throw new ArgumentNullException(nameof(drafts));
        _retryOptions = retryOptions ?? new SaveRetryOptions();
        ValidateRetryOptions(_retryOptions);
        _logger = logger ?? NullLogger<NoteSaveCoordinator>.Instance;
    }

    public async Task<SaveResult> CreateAsync(
        Note note,
        IReadOnlyCollection<Guid> tagIds,
        Guid? draftId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var validation = ValidateNoteInput(note, tagIds, draftId);
        if (validation is not null)
        {
            return validation;
        }

        if (note.Lifecycle != NoteLifecycle.Active)
        {
            return ValidationFailure(note.Id, draftId, "lifecycle_must_be_active");
        }

        return await SaveAsync(
            "create",
            note.Id,
            draftId,
            tagIds,
            token => _notes.CreateAsync(note, tagIds, token),
            exception => VerifyCreateAsync(exception, note, tagIds, draftId),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<SaveResult> UpdateAsync(
        Note note,
        long expectedVersion,
        IReadOnlyCollection<Guid> tagIds,
        Guid? draftId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var validation = ValidateNoteInput(note, tagIds, draftId);
        if (validation is not null)
        {
            return validation;
        }

        if (expectedVersion < 1)
        {
            return ValidationFailure(note.Id, draftId, "expected_version_invalid");
        }

        return await SaveAsync(
            "update",
            note.Id,
            draftId,
            tagIds,
            token => _notes.UpdateAsync(note, expectedVersion, tagIds, token),
            exception => VerifyUpdateAsync(exception, note, expectedVersion, tagIds, draftId),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<SaveResult> SetLifecycleAsync(
        Guid id,
        NoteLifecycle lifecycle,
        long expectedVersion,
        DateTimeOffset modifiedAtUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var validation = ValidateLifecycleInput(id, lifecycle, expectedVersion);
        if (validation is not null)
        {
            return validation;
        }

        return await SaveAsync(
            "set_lifecycle",
            id,
            draftId: null,
            draftTagIds: null,
            token => _notes.SetLifecycleAsync(id, lifecycle, expectedVersion, modifiedAtUtc, token),
            exception => VerifySetLifecycleAsync(exception, id, lifecycle, expectedVersion, modifiedAtUtc),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<SaveResult> SaveAsync(
        string operation,
        Guid noteId,
        Guid? draftId,
        IReadOnlyCollection<Guid>? draftTagIds,
        Func<CancellationToken, Task<Note>> write,
        Func<SaveOutcomeUnknownException, Task<SaveResult>> verifyCommit,
        CancellationToken cancellationToken)
    {
        var attempts = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempts++;
            var startedAt = Stopwatch.GetTimestamp();

            try
            {
                var saved = await write(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Save {Operation} for note {NoteId} succeeded on attempt {Attempt} in {DurationMs} ms.",
                    operation,
                    noteId,
                    attempts,
                    ElapsedMilliseconds(startedAt));
                var result = new SaveResult
                {
                    Status = SaveStatus.Saved,
                    NoteId = noteId,
                    SavedNote = saved,
                    Attempts = attempts,
                    DraftId = draftId,
                    DraftDisposition = draftId is null ? DraftDisposition.NotProvided : DraftDisposition.Retained
                };
                return await ApplyDraftCleanupAsync(result, saved, draftTagIds, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SaveOutcomeUnknownException exception)
            {
                _logger.LogWarning(
                    exception,
                    "Commit outcome unknown for {Operation} on note {NoteId} (attempt {Attempt}); verifying stored state.",
                    operation,
                    noteId,
                    attempts);
                var verified = await verifyCommit(exception).ConfigureAwait(false);
                return verified with { Attempts = attempts };
            }
            catch (StorageUnavailableException exception)
                when (exception.IsRetryable && attempts < _retryOptions.MaxAttempts)
            {
                var delay = _retryOptions.RetryDelays[attempts - 1];
                _logger.LogWarning(
                    exception,
                    "Transient {ErrorCode} during {Operation} for note {NoteId}; retrying in {DelayMs} ms after attempt {Attempt} of {MaxAttempts}.",
                    exception.ErrorCode,
                    operation,
                    noteId,
                    delay.TotalMilliseconds,
                    attempts,
                    _retryOptions.MaxAttempts);
                cancellationToken.ThrowIfCancellationRequested();
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (StorageUnavailableException exception)
            {
                var code = exception.IsRetryable
                    ? exception.ErrorCode switch
                    {
                        "sqlite_locked" => SaveErrorCode.StorageLocked,
                        _ => SaveErrorCode.StorageBusy
                    }
                    : SaveErrorCode.StorageUnavailable;
                _logger.LogWarning(
                    exception,
                    "Save {Operation} for note {NoteId} failed after {Attempt} attempt(s) with {ErrorCode}.",
                    operation,
                    noteId,
                    attempts,
                    exception.ErrorCode);
                return Failure(
                    SaveStatus.StorageUnavailable,
                    noteId,
                    draftId,
                    attempts,
                    new SaveError(
                        code,
                        $"Save {operation} failed because storage is unavailable.",
                        exception.IsRetryable,
                        $"operation={operation}; error={exception.ErrorCode}"));
            }
            catch (NoteConcurrencyException exception)
            {
                var current = await TryReadCurrentAsync(noteId).ConfigureAwait(false);
                _logger.LogWarning(
                    exception,
                    "Concurrency conflict during {Operation} for note {NoteId} (attempt {Attempt}).",
                    operation,
                    noteId,
                    attempts);
                return new SaveResult
                {
                    Status = SaveStatus.Conflict,
                    NoteId = noteId,
                    CurrentNote = current,
                    CurrentVersion = current?.Version ?? exception.ActualVersion,
                    Error = new SaveError(
                        SaveErrorCode.ConcurrencyConflict,
                        $"Concurrency conflict during {operation}.",
                        false,
                        $"operation={operation}; expected_version={exception.ExpectedVersion}; actual_version={exception.ActualVersion}"),
                    Attempts = attempts,
                    DraftId = draftId,
                    DraftDisposition = draftId is null ? DraftDisposition.NotProvided : DraftDisposition.Retained
                };
            }
            catch (NoteNotFoundException exception)
            {
                _logger.LogWarning(
                    exception,
                    "Note {NoteId} was not found during {Operation} (attempt {Attempt}).",
                    noteId,
                    operation,
                    attempts);
                return Failure(
                    SaveStatus.NotFound,
                    noteId,
                    draftId,
                    attempts,
                    new SaveError(
                        SaveErrorCode.NoteNotFound,
                        $"Note was not found during {operation}.",
                        false,
                        $"operation={operation}"));
            }
            catch (NoteConstraintException exception)
            {
                _logger.LogWarning(
                    exception,
                    "Constraint {ConstraintKind} rejected {Operation} for note {NoteId} (attempt {Attempt}).",
                    exception.ConstraintKind,
                    operation,
                    noteId,
                    attempts);
                return Failure(
                    SaveStatus.ValidationFailed,
                    noteId,
                    draftId,
                    attempts,
                    new SaveError(
                        SaveErrorCode.ValidationFailed,
                        $"The stored data rejected the {operation} operation.",
                        false,
                        $"constraint={exception.ConstraintKind}"));
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Unexpected error during {Operation} for note {NoteId} on attempt {Attempt}.",
                    operation,
                    noteId,
                    attempts);
                throw;
            }
        }
    }

    private async Task<SaveResult> ApplyDraftCleanupAsync(
        SaveResult result,
        Note saved,
        IReadOnlyCollection<Guid>? tagIds,
        CancellationToken cancellationToken)
    {
        if (result.DraftId is not { } draftId)
        {
            return result;
        }

        NoteDraft? draft;
        try
        {
            draft = await _drafts.GetAsync(draftId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Could not read draft {DraftId} after saving note {NoteId}: {ExceptionType} ({ErrorCode}).",
                draftId,
                saved.Id,
                exception.GetType().Name,
                GetDraftErrorCode(exception));
            return result with
            {
                DraftDisposition = DraftDisposition.CleanupFailed,
                DraftError = new SaveError(
                    SaveErrorCode.DraftCleanupFailed,
                    "The draft could not be read after the note was saved.",
                    false,
                    $"operation=get_draft; error={GetDraftErrorCode(exception)}")
            };
        }

        if (draft is null)
        {
            _logger.LogInformation(
                "Draft {DraftId} was already absent after saving note {NoteId}.",
                draftId,
                saved.Id);
            return result with { DraftDisposition = DraftDisposition.AlreadyAbsent };
        }

        if (!DraftMatches(draft, saved, tagIds))
        {
            _logger.LogWarning(
                "Draft {DraftId} does not match the saved content of note {NoteId}; retaining the draft.",
                draftId,
                saved.Id);
            return result with
            {
                DraftDisposition = DraftDisposition.Retained,
                DraftError = new SaveError(
                    SaveErrorCode.DraftMismatch,
                    "The draft does not match the saved note and was retained.",
                    false,
                    "draft_mismatch")
            };
        }

        try
        {
            await _drafts.DeleteAsync(draftId, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Draft {DraftId} was deleted after saving note {NoteId}.",
                draftId,
                saved.Id);
            return result with { DraftDisposition = DraftDisposition.DeletedAfterSave };
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Could not delete draft {DraftId} after saving note {NoteId}: {ExceptionType} ({ErrorCode}).",
                draftId,
                saved.Id,
                exception.GetType().Name,
                GetDraftErrorCode(exception));
            return result with
            {
                DraftDisposition = DraftDisposition.CleanupFailed,
                DraftError = new SaveError(
                    SaveErrorCode.DraftCleanupFailed,
                    "The draft could not be deleted after the note was saved.",
                    false,
                    $"operation=delete_draft; error={GetDraftErrorCode(exception)}")
            };
        }
    }

    private async Task<SaveResult> VerifyCreateAsync(
        SaveOutcomeUnknownException exception,
        Note note,
        IReadOnlyCollection<Guid> tagIds,
        Guid? draftId)
    {
        var readback = await ReadBackAsync(note.Id).ConfigureAwait(false);
        if (readback.Failure is not null)
        {
            return OutcomeUnknown(note.Id, exception, draftId, readback.Failure, readback.Note);
        }

        if (readback.Note is null)
        {
            return CommitFailedNotSaved(note.Id, exception, draftId);
        }

        var stored = readback.Note;
        var matches = stored.Version == 1
            && stored.Lifecycle == NoteLifecycle.Active
            && ContentMatches(stored, note)
            && SameInstant(stored.CreatedAtUtc, note.CreatedAtUtc)
            && SameInstant(stored.ModifiedAtUtc, note.ModifiedAtUtc)
            && TagSetsEqual(readback.TagIds ?? Array.Empty<Guid>(), tagIds);
        if (!matches)
        {
            return OutcomeUnknown(note.Id, exception, draftId, failure: null, stored);
        }

        _logger.LogWarning(
            exception,
            "Commit reported an error for create of note {NoteId}, but the stored row matches the input; treating the save as confirmed.",
            note.Id);
        var confirmed = SavedResult(note.Id, stored, draftId);
        return await ApplyDraftCleanupAsync(confirmed, stored, tagIds, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task<SaveResult> VerifyUpdateAsync(
        SaveOutcomeUnknownException exception,
        Note note,
        long expectedVersion,
        IReadOnlyCollection<Guid> tagIds,
        Guid? draftId)
    {
        var readback = await ReadBackAsync(note.Id).ConfigureAwait(false);
        if (readback.Failure is not null)
        {
            return OutcomeUnknown(note.Id, exception, draftId, readback.Failure, readback.Note);
        }

        if (readback.Note is null)
        {
            return NotFoundResult(note.Id, exception, draftId);
        }

        var stored = readback.Note;
        if (stored.Version == expectedVersion)
        {
            return CommitFailedNotSaved(note.Id, exception, draftId);
        }

        var matches = stored.Version == expectedVersion + 1
            && ContentMatches(stored, note)
            && SameInstant(stored.ModifiedAtUtc, note.ModifiedAtUtc)
            && TagSetsEqual(readback.TagIds ?? Array.Empty<Guid>(), tagIds);
        if (!matches)
        {
            return OutcomeUnknown(note.Id, exception, draftId, failure: null, stored);
        }

        _logger.LogWarning(
            exception,
            "Commit reported an error for update of note {NoteId}, but the stored row matches the input; treating the save as confirmed.",
            note.Id);
        var confirmed = SavedResult(note.Id, stored, draftId);
        return await ApplyDraftCleanupAsync(confirmed, stored, tagIds, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task<SaveResult> VerifySetLifecycleAsync(
        SaveOutcomeUnknownException exception,
        Guid id,
        NoteLifecycle lifecycle,
        long expectedVersion,
        DateTimeOffset modifiedAtUtc)
    {
        var readback = await ReadBackAsync(id).ConfigureAwait(false);
        if (readback.Failure is not null)
        {
            return OutcomeUnknown(id, exception, draftId: null, readback.Failure, readback.Note);
        }

        if (readback.Note is null)
        {
            return NotFoundResult(id, exception, draftId: null);
        }

        var stored = readback.Note;
        if (stored.Version == expectedVersion)
        {
            return CommitFailedNotSaved(id, exception, draftId: null);
        }

        var matches = stored.Version == expectedVersion + 1
            && stored.Lifecycle == lifecycle
            && stored.DisplayIntent == NoteDisplayIntent.Hidden
            && SameInstant(stored.ModifiedAtUtc, modifiedAtUtc);
        if (!matches)
        {
            return OutcomeUnknown(id, exception, draftId: null, failure: null, stored);
        }

        _logger.LogWarning(
            exception,
            "Commit reported an error for lifecycle change of note {NoteId}, but the stored row matches the request; treating the save as confirmed.",
            id);
        return SavedResult(id, stored, draftId: null);
    }

    private async Task<ReadBackResult> ReadBackAsync(Guid noteId)
    {
        Note? note = null;
        try
        {
            note = await _notes.GetAsync(noteId, CancellationToken.None).ConfigureAwait(false);
            if (note is null)
            {
                return new ReadBackResult(null, null, null);
            }

            var tagIds = await _notes.GetTagIdsAsync(noteId, CancellationToken.None).ConfigureAwait(false);
            return new ReadBackResult(note, tagIds, null);
        }
        catch (Exception exception)
        {
            return new ReadBackResult(note, null, exception);
        }
    }

    private async Task<Note?> TryReadCurrentAsync(Guid noteId)
    {
        try
        {
            return await _notes.GetAsync(noteId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Could not read current note {NoteId} after a conflict: {ExceptionType}.",
                noteId,
                exception.GetType().Name);
            return null;
        }
    }

    private SaveResult OutcomeUnknown(
        Guid noteId,
        SaveOutcomeUnknownException exception,
        Guid? draftId,
        Exception? failure,
        Note? currentNote)
    {
        var diagnostics = AppendRollbackDiagnostic(
            exception,
            failure is null ? "state_mismatch" : $"read_failure={DescribeFailure(failure)}");
        _logger.LogWarning(
            exception,
            "Could not verify commit outcome for {Operation} on note {NoteId} ({Diagnostics}); reporting outcome unknown.",
            exception.Operation,
            noteId,
            diagnostics);
        return new SaveResult
        {
            Status = SaveStatus.OutcomeUnknown,
            NoteId = noteId,
            CurrentNote = currentNote,
            CurrentVersion = currentNote?.Version,
            Error = new SaveError(
                SaveErrorCode.CommitOutcomeUnknown,
                "The commit outcome could not be verified; the save must not be treated as successful.",
                false,
                diagnostics),
            Attempts = 0,
            DraftId = draftId,
            DraftDisposition = draftId is null ? DraftDisposition.NotProvided : DraftDisposition.Retained
        };
    }

    private SaveResult CommitFailedNotSaved(
        Guid noteId,
        SaveOutcomeUnknownException exception,
        Guid? draftId)
    {
        _logger.LogWarning(
            exception,
            "Commit for {Operation} on note {NoteId} failed before writing; the note was not saved.",
            exception.Operation,
            noteId);
        return Failure(
            SaveStatus.StorageUnavailable,
            noteId,
            draftId,
            0,
            new SaveError(
                SaveErrorCode.CommitFailedNotSaved,
                "The commit failed and the note was not saved.",
                true,
                AppendRollbackDiagnostic(exception, "commit_not_saved")));
    }

    private SaveResult NotFoundResult(
        Guid noteId,
        SaveOutcomeUnknownException exception,
        Guid? draftId)
    {
        _logger.LogWarning(
            exception,
            "Note {NoteId} was not found after an unknown commit for {Operation}.",
            noteId,
            exception.Operation);
        return Failure(
            SaveStatus.NotFound,
            noteId,
            draftId,
            0,
            new SaveError(
                SaveErrorCode.NoteNotFound,
                $"Note was not found after {exception.Operation}.",
                false,
                AppendRollbackDiagnostic(exception, $"operation={exception.Operation}")));
    }

    private static SaveResult SavedResult(Guid noteId, Note note, Guid? draftId) => new()
    {
        Status = SaveStatus.Saved,
        NoteId = noteId,
        SavedNote = note,
        Attempts = 0,
        DraftId = draftId,
        DraftDisposition = draftId is null ? DraftDisposition.NotProvided : DraftDisposition.Retained
    };

    private static SaveResult Failure(
        SaveStatus status,
        Guid noteId,
        Guid? draftId,
        int attempts,
        SaveError error) => new()
    {
        Status = status,
        NoteId = noteId,
        Error = error,
        Attempts = attempts,
        DraftId = draftId,
        DraftDisposition = draftId is null ? DraftDisposition.NotProvided : DraftDisposition.Retained
    };

    private static SaveResult? ValidateNoteInput(
        Note? note,
        IReadOnlyCollection<Guid>? tagIds,
        Guid? draftId)
    {
        if (note is null)
        {
            return ValidationFailure(Guid.Empty, draftId, "note_null");
        }

        if (note.Id == Guid.Empty)
        {
            return ValidationFailure(note.Id, draftId, "note_id_empty");
        }

        if (note.CategoryId == Guid.Empty)
        {
            return ValidationFailure(note.Id, draftId, "category_id_empty");
        }

        if (note.Body is null)
        {
            return ValidationFailure(note.Id, draftId, "body_null");
        }

        if (!Enum.IsDefined(note.DisplayIntent))
        {
            return ValidationFailure(note.Id, draftId, "display_intent_undefined");
        }

        if (tagIds is null)
        {
            return ValidationFailure(note.Id, draftId, "tag_ids_null");
        }

        if (tagIds.Any(id => id == Guid.Empty))
        {
            return ValidationFailure(note.Id, draftId, "tag_id_empty");
        }

        if (draftId == Guid.Empty)
        {
            return ValidationFailure(note.Id, draftId, "draft_id_empty");
        }

        return null;
    }

    private static SaveResult? ValidateLifecycleInput(
        Guid id,
        NoteLifecycle lifecycle,
        long expectedVersion)
    {
        if (id == Guid.Empty)
        {
            return ValidationFailure(id, draftId: null, "note_id_empty");
        }

        if (!Enum.IsDefined(lifecycle))
        {
            return ValidationFailure(id, draftId: null, "lifecycle_undefined");
        }

        if (expectedVersion < 1)
        {
            return ValidationFailure(id, draftId: null, "expected_version_invalid");
        }

        return null;
    }

    private static SaveResult ValidationFailure(Guid noteId, Guid? draftId, string reason) => new()
    {
        Status = SaveStatus.ValidationFailed,
        NoteId = noteId,
        Error = new SaveError(
            SaveErrorCode.ValidationFailed,
            $"Input validation failed: {reason}.",
            false,
            reason),
        Attempts = 0,
        DraftId = draftId,
        DraftDisposition = draftId is null ? DraftDisposition.NotProvided : DraftDisposition.Retained
    };

    private static void ValidateRetryOptions(SaveRetryOptions options)
    {
        if (options.MaxAttempts < 1)
        {
            throw new ArgumentException("MaxAttempts must be at least 1.", nameof(options));
        }

        if (options.RetryDelays is null)
        {
            throw new ArgumentException("RetryDelays must not be null.", nameof(options));
        }

        if (options.RetryDelays.Count < options.MaxAttempts - 1)
        {
            throw new ArgumentException(
                "RetryDelays must contain at least MaxAttempts - 1 entries.",
                nameof(options));
        }

        if (options.RetryDelays.Any(delay => delay < TimeSpan.Zero))
        {
            throw new ArgumentException("RetryDelays must be non-negative.", nameof(options));
        }
    }

    private static bool ContentMatches(Note stored, Note input) =>
        string.Equals(stored.Title, input.Title, StringComparison.Ordinal)
        && string.Equals(stored.Body, input.Body, StringComparison.Ordinal)
        && stored.CategoryId == input.CategoryId
        && stored.IsPinned == input.IsPinned
        && stored.DisplayIntent == input.DisplayIntent;

    private static bool DraftMatches(NoteDraft draft, Note saved, IReadOnlyCollection<Guid>? tagIds) =>
        draft.NoteId == saved.Id
        && string.Equals(draft.Title, saved.Title, StringComparison.Ordinal)
        && string.Equals(draft.Body, saved.Body, StringComparison.Ordinal)
        && draft.CategoryId == saved.CategoryId
        && draft.IsPinned == saved.IsPinned
        && draft.DisplayIntent == saved.DisplayIntent
        && TagSetsEqual(draft.TagIds ?? Array.Empty<Guid>(), tagIds ?? Array.Empty<Guid>());

    private static bool TagSetsEqual(IReadOnlyCollection<Guid> left, IReadOnlyCollection<Guid> right) =>
        new HashSet<Guid>(left).SetEquals(right);

    private static bool SameInstant(DateTimeOffset left, DateTimeOffset right) =>
        left.UtcDateTime == right.UtcDateTime;

    private static string GetDraftErrorCode(Exception exception) =>
        exception is DraftStoreException draftException
            ? draftException.ErrorCode
            : exception.GetType().Name;

    private static string DescribeFailure(Exception exception) =>
        exception is NoteStorageException storageException
            ? $"{exception.GetType().Name}:{storageException.ErrorCode}"
            : exception.GetType().Name;

    private static string AppendRollbackDiagnostic(Exception exception, string diagnostics) =>
        exception.Data["rollback_error"] is string rollbackError
            ? $"{diagnostics}; rollback_error={rollbackError}"
            : diagnostics;

    private static double ElapsedMilliseconds(long startedAt) =>
        Math.Round(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, 1);

    private readonly record struct ReadBackResult(Note? Note, IReadOnlyList<Guid>? TagIds, Exception? Failure);
}
