using QuickNotes.Application;
using QuickNotes.Domain;

namespace QuickNotes.IntegrationTests.C6;

/// <summary>
/// Test-only save fault double used by the C6 harness scenario tests. It never
/// exists in production composition; Task 2 consumes it from FaultDecoratorTests.
/// The result mirrors <see cref="NoteSaveCoordinator"/> draft semantics: a save
/// without a draft ID never claims a retained draft, and a success reports the
/// single attempt this fake actually performs.
/// </summary>
internal sealed class FaultingNoteSaveCoordinator : INoteSaveCoordinator
{
    private readonly Func<CancellationToken, Task> _fault;

    public FaultingNoteSaveCoordinator(Func<CancellationToken, Task> fault)
    {
        _fault = fault ?? throw new ArgumentNullException(nameof(fault));
    }

    public Task<SaveResult> CreateAsync(
        Note note,
        IReadOnlyCollection<Guid> tagIds,
        Guid? draftId = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync("create", note, draftId, cancellationToken);

    public Task<SaveResult> UpdateAsync(
        Note note,
        long expectedVersion,
        IReadOnlyCollection<Guid> tagIds,
        Guid? draftId = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync("update", note, draftId, cancellationToken);

    public Task<SaveResult> SetLifecycleAsync(
        Guid id,
        NoteLifecycle lifecycle,
        long expectedVersion,
        DateTimeOffset modifiedAtUtc,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "set_lifecycle",
            new Note
            {
                Id = id,
                Body = string.Empty,
                CreatedAtUtc = modifiedAtUtc,
                ModifiedAtUtc = modifiedAtUtc,
                CategoryId = Category.UncategorizedId,
                Lifecycle = lifecycle
            },
            draftId: null,
            cancellationToken);

    private async Task<SaveResult> ExecuteAsync(
        string operation,
        Note note,
        Guid? draftId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await _fault(cancellationToken);
        }
        catch (IOException exception)
        {
            return Failure(
                SaveErrorCode.StorageUnavailable,
                operation,
                note.Id,
                draftId,
                isRetryable: false,
                exception.Message);
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
            return Failure(
                code,
                operation,
                note.Id,
                draftId,
                exception.IsRetryable,
                exception.ErrorCode);
        }

        if (draftId is not null)
        {
            // The real coordinator reads, compares, and deletes the draft
            // after a successful save. This double has no draft store, so it
            // refuses to invent a draft disposition instead of guessing.
            throw new NotImplementedException(
                "FaultingNoteSaveCoordinator cannot model a successful save that owns a draft; inject a fault for draft scenarios.");
        }

        return new SaveResult
        {
            Status = SaveStatus.Saved,
            NoteId = note.Id,
            SavedNote = note,
            Attempts = 1,
            DraftId = null,
            DraftDisposition = DraftDisposition.NotProvided
        };
    }

    private static SaveResult Failure(
        SaveErrorCode code,
        string operation,
        Guid noteId,
        Guid? draftId,
        bool isRetryable,
        string diagnostics) =>
        new()
        {
            Status = SaveStatus.StorageUnavailable,
            NoteId = noteId,
            Error = new SaveError(
                code,
                $"Save {operation} failed because storage is unavailable.",
                isRetryable,
                $"operation={operation}; error={diagnostics}"),
            Attempts = 1,
            DraftId = draftId,
            // Real NoteSaveCoordinator never claims Retained when no draft was
            // supplied; the fake must not make a failed save look draft-safe.
            DraftDisposition = draftId is null
                ? DraftDisposition.NotProvided
                : DraftDisposition.Retained
        };
}
