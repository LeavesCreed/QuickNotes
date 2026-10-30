using QuickNotes.Application;
using QuickNotes.Domain;

namespace QuickNotes.IntegrationTests.C6;

public sealed class C6FaultFakesTests
{
    private static readonly Guid NoteId = Guid.Parse("c6000000-0003-0000-0000-000000000001");

    [Fact]
    public async Task FaultingSave_WithoutDraft_FailureDoesNotClaimARetainedDraft()
    {
        var coordinator = new FaultingNoteSaveCoordinator(
            _ => throw new IOException("disk full"));

        var result = await coordinator.CreateAsync(NewNote(), Array.Empty<Guid>());

        Assert.Equal(SaveStatus.StorageUnavailable, result.Status);
        Assert.Equal(SaveErrorCode.StorageUnavailable, result.Error!.Code);
        Assert.False(result.Error.IsRetryable);
        Assert.Equal(1, result.Attempts);
        Assert.Null(result.DraftId);
        // Real NoteSaveCoordinator: no draft supplied => NotProvided, never Retained.
        Assert.Equal(DraftDisposition.NotProvided, result.DraftDisposition);
        Assert.Contains("disk full", result.Error.Diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FaultingSave_WithDraft_FailureRetainsTheDraft()
    {
        var coordinator = new FaultingNoteSaveCoordinator(
            _ => throw new IOException("disk full"));
        var draftId = Guid.NewGuid();

        var result = await coordinator.UpdateAsync(
            NewNote(),
            expectedVersion: 2,
            Array.Empty<Guid>(),
            draftId);

        Assert.Equal(SaveStatus.StorageUnavailable, result.Status);
        Assert.Equal(draftId, result.DraftId);
        Assert.Equal(DraftDisposition.Retained, result.DraftDisposition);
        Assert.Equal(1, result.Attempts);
    }

    [Fact]
    public async Task FaultingSave_InjectedStorageError_UsesItsErrorCodeAndRetryability()
    {
        var coordinator = new FaultingNoteSaveCoordinator(
            _ => throw new StorageUnavailableException(
                "update",
                NoteId,
                "sqlite_locked",
                isRetryable: true,
                "database is locked"));

        var result = await coordinator.UpdateAsync(
            NewNote(),
            expectedVersion: 2,
            Array.Empty<Guid>());

        Assert.Equal(SaveStatus.StorageUnavailable, result.Status);
        Assert.Equal(SaveErrorCode.StorageLocked, result.Error!.Code);
        Assert.True(result.Error.IsRetryable);
        Assert.Contains("sqlite_locked", result.Error.Diagnostics, StringComparison.Ordinal);
        Assert.Equal(DraftDisposition.NotProvided, result.DraftDisposition);
    }

    [Fact]
    public async Task FaultingSave_SuccessWithoutDraft_ReportsTheSinglePerformedAttempt()
    {
        var coordinator = new FaultingNoteSaveCoordinator(_ => Task.CompletedTask);
        var note = NewNote();

        var result = await coordinator.CreateAsync(note, Array.Empty<Guid>());

        Assert.Equal(SaveStatus.Saved, result.Status);
        Assert.Equal(note.Id, result.NoteId);
        Assert.Same(note, result.SavedNote);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(DraftDisposition.NotProvided, result.DraftDisposition);
    }

    [Fact]
    public async Task FaultingSave_SuccessWithDraft_IsRefusedInsteadOfGuessingADisposition()
    {
        var coordinator = new FaultingNoteSaveCoordinator(_ => Task.CompletedTask);

        await Assert.ThrowsAsync<NotImplementedException>(
            () => coordinator.CreateAsync(NewNote(), Array.Empty<Guid>(), Guid.NewGuid()));
    }

    [Fact]
    public async Task FaultingSave_SetLifecycleFailure_HasNoDraft()
    {
        var coordinator = new FaultingNoteSaveCoordinator(
            _ => throw new IOException("disk full"));

        var result = await coordinator.SetLifecycleAsync(
            NoteId,
            NoteLifecycle.Archived,
            expectedVersion: 1,
            DateTimeOffset.UnixEpoch);

        Assert.Equal(SaveStatus.StorageUnavailable, result.Status);
        Assert.Null(result.DraftId);
        Assert.Equal(DraftDisposition.NotProvided, result.DraftDisposition);
    }

    private static Note NewNote() => new()
    {
        Id = NoteId,
        Title = "C6 fault fake note",
        Body = "C6-SYNTHETIC run=fault-fake body",
        CreatedAtUtc = DateTimeOffset.UnixEpoch,
        ModifiedAtUtc = DateTimeOffset.UnixEpoch,
        CategoryId = Category.UncategorizedId
    };
}
