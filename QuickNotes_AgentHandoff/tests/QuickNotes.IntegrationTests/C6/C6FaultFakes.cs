using QuickNotes.Application;
using QuickNotes.Domain;

namespace QuickNotes.IntegrationTests;

/// <summary>
/// Test-only save fault double used by the C6 harness scenario tests. It never
/// exists in production composition; Task 2 consumes it from FaultDecoratorTests.
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
        ExecuteAsync("create", note, cancellationToken);

    public Task<SaveResult> UpdateAsync(
        Note note,
        long expectedVersion,
        IReadOnlyCollection<Guid> tagIds,
        Guid? draftId = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync("update", note, cancellationToken);

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
            cancellationToken);

    private async Task<SaveResult> ExecuteAsync(
        string operation,
        Note note,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await _fault(cancellationToken);
            return new SaveResult
            {
                Status = SaveStatus.Saved,
                NoteId = note.Id,
                SavedNote = note,
                DraftDisposition = DraftDisposition.NotProvided
            };
        }
        catch (IOException exception)
        {
            return Failure(note.Id, operation, exception.Message);
        }
        catch (StorageUnavailableException exception)
        {
            return Failure(note.Id, operation, exception.ErrorCode);
        }
    }

    private static SaveResult Failure(Guid noteId, string operation, string diagnostics) =>
        new()
        {
            Status = SaveStatus.StorageUnavailable,
            NoteId = noteId,
            Error = new SaveError(
                SaveErrorCode.StorageUnavailable,
                $"Save {operation} failed because storage is unavailable.",
                IsRetryable: false,
                Diagnostics: $"operation={operation}; error={diagnostics}"),
            DraftDisposition = DraftDisposition.Retained
        };
}
