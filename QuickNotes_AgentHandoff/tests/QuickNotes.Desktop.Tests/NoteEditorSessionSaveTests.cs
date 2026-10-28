using QuickNotes.Application;
using QuickNotes.Desktop.Editing;
using QuickNotes.Domain;

namespace QuickNotes.Desktop.Tests;

public sealed class NoteEditorSessionSaveTests
{
    [Fact]
    public async Task Save_Success_MarksSavedAndAdvancesBaseVersion()
    {
        var (workspace, notes, saves, _) = CreateWorkspace();
        var note = EditorTestData.NewNote(version: 1, body: "original");
        notes.Add(note);
        var session = await workspace.OpenAsync(note.Id);
        session!.ChangeText("title", "updated");

        var result = await session.SaveNowAsync();

        Assert.Equal(SaveStatus.Saved, result.Status);
        Assert.True(
            session.Status.SaveState == EditorSaveState.Saved,
            $"state={session.Status.SaveState};seq={session.Status.Sequence};error={session.Status.ErrorCode};message={session.Status.Message}");
        Assert.Equal(2, session.Status.BaseVersion);
        Assert.False(session.Status.HasPendingChanges);
        Assert.Equal(1, saves.UpdateCalls);
        Assert.Equal(1, saves.Calls[0].ExpectedVersion);
        Assert.Equal("updated", saves.Calls[0].Note.Body);
    }

    [Fact]
    public async Task Save_WhileNewerEditQueued_DoesNotMarkNewerEditSaved()
    {
        var (workspace, notes, saves, _) = CreateWorkspace();
        var note = EditorTestData.NewNote(version: 1, body: "original");
        notes.Add(note);
        var session = await workspace.OpenAsync(note.Id);
        session!.ChangeText("title", "first");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<SaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Note? captured = null;
        Guid? capturedDraftId = null;
        saves.UpdateHandler = (saving, _, _, draftId, _) =>
        {
            captured = saving;
            capturedDraftId = draftId;
            entered.TrySetResult();
            return completion.Task;
        };

        var firstSave = session.SaveNowAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        session.ChangeText("title", "second");
        completion.SetResult(FakeSaveCoordinator.SavedResult(
            captured! with { Version = 2 },
            capturedDraftId));
        await firstSave;

        Assert.Equal(EditorSaveState.Modified, session.Status.SaveState);
        Assert.True(session.Status.HasPendingChanges);
        Assert.Equal("second", session.Body);
        Assert.Equal(2, session.Status.BaseVersion);
    }

    [Fact]
    public async Task Save_StaleResult_DoesNotClearNewerModifiedState()
    {
        var (workspace, notes, saves, _) = CreateWorkspace();
        var note = EditorTestData.NewNote(version: 1);
        notes.Add(note);
        var session = await workspace.OpenAsync(note.Id);
        session!.ChangeText("title", "first");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<SaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Note? captured = null;
        Guid? capturedDraftId = null;
        saves.UpdateHandler = (saving, _, _, draftId, _) =>
        {
            captured = saving;
            capturedDraftId = draftId;
            entered.TrySetResult();
            return completion.Task;
        };

        var save = session.SaveNowAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        session.ChangeText("title", "second");
        completion.SetResult(FakeSaveCoordinator.SavedResult(
            captured! with { Body = "first", Version = 2 },
            capturedDraftId));
        await save;

        Assert.Equal(EditorSaveState.Modified, session.Status.SaveState);
        Assert.Equal("second", session.Body);
    }

    [Fact]
    public async Task SecondSaveWaitsForFirst_AndUsesLatestSnapshot()
    {
        var (workspace, notes, saves, _) = CreateWorkspace();
        var note = EditorTestData.NewNote(version: 1);
        notes.Add(note);
        var session = await workspace.OpenAsync(note.Id);
        session!.ChangeText("title", "first");
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new TaskCompletionSource<SaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<SaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        saves.UpdateHandler = (saving, _, _, draftId, _) =>
        {
            if (Interlocked.Increment(ref callCount) == 1)
            {
                firstEntered.TrySetResult();
                return first.Task;
            }

            secondEntered.TrySetResult();
            return second.Task;
        };

        var firstSave = session.SaveNowAsync();
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        session.ChangeText("title", "second");
        var secondSave = session.SaveNowAsync();
        await Task.Delay(50);
        Assert.False(secondEntered.Task.IsCompleted);
        first.SetResult(FakeSaveCoordinator.SavedResult(
            note with { Body = "first", Version = 2 },
            session.DraftId));
        await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        second.SetResult(FakeSaveCoordinator.SavedResult(
            note with { Body = "second", Version = 3 },
            session.DraftId));
        await Task.WhenAll(firstSave, secondSave);

        Assert.Equal(2, saves.UpdateCalls);
        Assert.Equal(2, saves.Calls[1].ExpectedVersion);
        Assert.Equal(EditorSaveState.Saved, session.Status.SaveState);
        Assert.Equal(3, session.Status.BaseVersion);
    }

    [Fact]
    public async Task Conflict_PreservesLocalSnapshotAndDraft()
    {
        var (workspace, notes, saves, drafts) = CreateWorkspace();
        var note = EditorTestData.NewNote(version: 1, body: "original");
        notes.Add(note);
        var session = await workspace.OpenAsync(note.Id);
        session!.ChangeText("title", "local");
        saves.UpdateHandler = (_, _, _, draftId, _) => Task.FromResult(ConflictResult(
            note with { Body = "remote", Version = 2 },
            draftId));

        await session.SaveNowAsync();

        Assert.Equal(EditorSaveState.Conflict, session.Status.SaveState);
        Assert.NotNull(session.Conflict);
        Assert.Equal("local", session.Body);
        Assert.True(drafts.Drafts.ContainsKey(session.DraftId));
    }

    [Fact]
    public async Task Conflict_Reload_LoadsRemoteVersion()
    {
        var (workspace, notes, saves, _) = CreateWorkspace();
        var note = EditorTestData.NewNote(version: 1, body: "original");
        notes.Add(note);
        var session = await workspace.OpenAsync(note.Id);
        session!.ChangeText("title", "local");
        saves.UpdateHandler = (_, _, _, draftId, _) => Task.FromResult(ConflictResult(
            note with { Body = "remote", Version = 2 },
            draftId));
        await session.SaveNowAsync();
        notes.Add(note with { Body = "remote", Title = "remote title", Version = 2 });

        await session.ReloadAsync();

        Assert.Equal(EditorSaveState.Clean, session.Status.SaveState);
        Assert.Equal("remote", session.Body);
        Assert.Equal(2, session.Status.BaseVersion);
        Assert.Null(session.Conflict);
    }

    [Fact]
    public async Task Conflict_SaveAsNew_CreatesDistinctNote()
    {
        var (workspace, notes, saves, _) = CreateWorkspace();
        var note = EditorTestData.NewNote(version: 1, body: "original");
        notes.Add(note);
        var session = await workspace.OpenAsync(note.Id);
        session!.ChangeText("title", "local");
        saves.UpdateHandler = (_, _, _, draftId, _) => Task.FromResult(ConflictResult(
            note with { Body = "remote", Version = 2 },
            draftId));
        await session.SaveNowAsync();
        saves.CreateHandler = (saving, tagIds, draftId, _) =>
        {
            var created = saving with { Version = 1 };
            notes.Add(created, tagIds);
            return Task.FromResult(FakeSaveCoordinator.SavedResult(created, draftId));
        };

        var savedAsNew = await session.SaveAsNewAsync();

        Assert.NotEqual(session.NoteId, savedAsNew.NoteId);
        Assert.NotEqual(session.DraftId, savedAsNew.DraftId);
        Assert.Equal(EditorSaveState.Saved, savedAsNew.Status.SaveState);
        Assert.Equal(1, saves.CreateCalls);
        Assert.Equal("local", notes.Notes[savedAsNew.NoteId].Body);
    }

    [Fact]
    public async Task OutcomeUnknown_Retry_ReadbackMatchingContentMarksSaved()
    {
        var (workspace, notes, saves, _) = CreateWorkspace();
        var note = EditorTestData.NewNote(version: 1, body: "original");
        notes.Add(note);
        var session = await workspace.OpenAsync(note.Id);
        session!.ChangeText("title", "local");
        saves.UpdateHandler = (_, _, _, draftId, _) => Task.FromResult(UnknownResult(note, draftId));
        await session.SaveNowAsync();
        notes.Add(note with { Body = "local", Version = 2 });

        var resolution = await session.RetryAsync();

        Assert.Equal(EditorSaveResolutionStatus.Saved, resolution.Status);
        Assert.Equal(EditorSaveState.Saved, session.Status.SaveState);
        Assert.Equal(2, session.Status.BaseVersion);
    }

    [Fact]
    public async Task OutcomeUnknown_Retry_NoStoredNote_CreatesWithoutDuplicate()
    {
        var (workspace, notes, saves, _) = CreateWorkspace();
        var session = await workspace.CreateNewAsync();
        session.ChangeText("title", "local");
        var calls = 0;
        saves.CreateHandler = (saving, tagIds, draftId, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                return Task.FromResult(UnknownResult(saving, draftId));
            }

            var created = saving with { Version = 1 };
            notes.Add(created, tagIds);
            return Task.FromResult(FakeSaveCoordinator.SavedResult(created, draftId));
        };
        await session.SaveNowAsync();

        var resolution = await session.RetryAsync();

        Assert.Equal(EditorSaveResolutionStatus.Saved, resolution.Status);
        Assert.Equal(2, saves.CreateCalls);
        Assert.Single(notes.Notes);
        Assert.Equal(session.NoteId, notes.Notes.Keys.Single());
    }

    [Fact]
    public async Task OutcomeUnknown_Retry_StoredDifferentContent_ReportsConflict()
    {
        var (workspace, notes, saves, _) = CreateWorkspace();
        var note = EditorTestData.NewNote(version: 1, body: "original");
        notes.Add(note);
        var session = await workspace.OpenAsync(note.Id);
        session!.ChangeText("title", "local");
        saves.UpdateHandler = (_, _, _, draftId, _) => Task.FromResult(UnknownResult(note, draftId));
        await session.SaveNowAsync();
        notes.Add(note with { Body = "remote", Version = 2 });

        var resolution = await session.RetryAsync();

        Assert.Equal(EditorSaveResolutionStatus.Conflict, resolution.Status);
        Assert.Equal(EditorSaveState.Conflict, session.Status.SaveState);
    }

    [Fact]
    public async Task SaveFailure_RetainsDraftAndState()
    {
        var (workspace, notes, saves, drafts) = CreateWorkspace();
        var note = EditorTestData.NewNote(version: 1);
        notes.Add(note);
        var session = await workspace.OpenAsync(note.Id);
        session!.ChangeText("title", "local");
        saves.UpdateHandler = (_, _, _, draftId, _) => Task.FromResult(new SaveResult
        {
            Status = SaveStatus.StorageUnavailable,
            NoteId = note.Id,
            DraftId = draftId,
            Error = new SaveError(
                SaveErrorCode.StorageUnavailable,
                "simulated storage failure",
                false,
                "test")
        });

        await session.SaveNowAsync();

        Assert.Equal(EditorSaveState.SaveFailed, session.Status.SaveState);
        Assert.True(session.Status.HasPendingChanges);
        Assert.True(drafts.Drafts.ContainsKey(session.DraftId));
    }

    private static (EditorWorkspaceService Workspace, FakeNoteRepository Notes, FakeSaveCoordinator Saves, FakeDraftStore Drafts)
        CreateWorkspace()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var saves = new FakeSaveCoordinator();
        return (
            new EditorWorkspaceService(
                notes,
                saves,
                drafts,
                EditorTestData.DraftOnlyOptions(),
                new TestTimeProvider(EditorTestData.Timestamp)),
            notes,
            saves,
            drafts);
    }

    private static SaveResult ConflictResult(Note remote, Guid? draftId) => new()
    {
        Status = SaveStatus.Conflict,
        NoteId = remote.Id,
        CurrentNote = remote,
        CurrentVersion = remote.Version,
        DraftId = draftId,
        Error = new SaveError(
            SaveErrorCode.ConcurrencyConflict,
            "simulated conflict",
            false,
            "test")
    };

    private static SaveResult UnknownResult(Note note, Guid? draftId) => new()
    {
        Status = SaveStatus.OutcomeUnknown,
        NoteId = note.Id,
        DraftId = draftId,
        Error = new SaveError(
            SaveErrorCode.CommitOutcomeUnknown,
            "simulated unknown outcome",
            false,
            "test")
    };
}
