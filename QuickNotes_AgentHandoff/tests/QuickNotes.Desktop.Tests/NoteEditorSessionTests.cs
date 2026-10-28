using QuickNotes.Desktop.Editing;
using QuickNotes.Domain;

namespace QuickNotes.Desktop.Tests;

public sealed class NoteEditorSessionTests
{
    [Fact]
    public async Task CreateNew_StableNoteAndDraftIds()
    {
        var (workspace, _, _, _) = CreateWorkspace();

        var session = await workspace.CreateNewAsync();

        Assert.NotEqual(Guid.Empty, session.NoteId);
        Assert.NotEqual(Guid.Empty, session.DraftId);
        Assert.True(session.IsNew);
        Assert.Equal(EditorSaveState.Clean, session.Status.SaveState);
        Assert.False(session.Status.HasPendingChanges);
    }

    [Fact]
    public async Task ChangeText_IncrementsSequenceAndMarksModified()
    {
        var (workspace, _, _, _) = CreateWorkspace();
        var session = await workspace.CreateNewAsync();

        session.ChangeText("new title", "new body");

        var status = session.Status;
        Assert.Equal(1, status.Sequence);
        Assert.Equal(EditorSaveState.Modified, status.SaveState);
        Assert.Equal(DraftProtectionState.Pending, status.DraftState);
        Assert.True(status.HasPendingChanges);
    }

    [Fact]
    public async Task ChangeText_CapturesDraftAfterDebounce()
    {
        var options = EditorTestData.DraftOnlyOptions();
        var (workspace, notes, drafts, _) = CreateWorkspace(options: options);
        var session = await workspace.CreateNewAsync();

        session.ChangeText("draft title", "draft body");
        await drafts.SavedOnce.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => session.Status.DraftState == DraftProtectionState.Protected);

        var draft = drafts.Drafts[session.DraftId];
        Assert.Equal("draft title", draft.Title);
        Assert.Equal("draft body", draft.Body);
        Assert.Equal(0, draft.BaseVersion);
        Assert.Empty(notes.Notes);
    }

    [Fact]
    public async Task DraftFailure_LeavesContentAndReportsUnprotected()
    {
        var (workspace, _, drafts, _) = CreateWorkspace();
        drafts.FailOnSave = true;
        var session = await workspace.CreateNewAsync();
        session.ChangeText("title", "content");

        await session.FlushDraftAsync();

        Assert.Equal("content", session.Body);
        Assert.Equal(DraftProtectionState.Failed, session.Status.DraftState);
        Assert.False(string.IsNullOrWhiteSpace(session.Status.ErrorCode));
    }

    [Fact]
    public async Task OpenExisting_LoadsTitleBodyVersionAndTags()
    {
        var (workspace, notes, _, _) = CreateWorkspace();
        var tagId = Guid.NewGuid();
        var note = EditorTestData.NewNote(version: 3, body: "stored body");
        notes.Add(note, new[] { tagId });

        var session = await workspace.OpenAsync(note.Id);

        Assert.NotNull(session);
        Assert.False(session!.IsNew);
        Assert.Equal("title", session.Title);
        Assert.Equal("stored body", session.Body);
        Assert.Equal(3, session.Status.BaseVersion);
        Assert.Equal(DraftProtectionState.NotNeeded, session.Status.DraftState);
    }

    [Fact]
    public async Task NewSession_DraftHasBaseVersionZero()
    {
        var (workspace, _, drafts, _) = CreateWorkspace();
        var session = await workspace.CreateNewAsync();
        session.ChangeText(null, "new");

        await session.FlushDraftAsync();

        Assert.Equal(0, drafts.Drafts[session.DraftId].BaseVersion);
    }

    [Fact]
    public async Task ExistingSession_DraftPreservesBaseVersion()
    {
        var (workspace, notes, drafts, _) = CreateWorkspace();
        var note = EditorTestData.NewNote(version: 3);
        notes.Add(note);
        var session = await workspace.OpenAsync(note.Id);
        session!.ChangeText("changed", "changed body");

        await session.FlushDraftAsync();

        Assert.Equal(3, drafts.Drafts[session.DraftId].BaseVersion);
    }

    [Fact]
    public async Task Dispose_CancelsPendingDebounceWithoutDeletingDraft()
    {
        var options = new EditorAutosaveOptions
        {
            DraftDebounce = TimeSpan.FromHours(1),
            DraftMaxDelay = TimeSpan.FromHours(2),
            SaveDebounce = TimeSpan.FromHours(1),
            SaveMaxDelay = TimeSpan.FromHours(2)
        };
        var (workspace, _, drafts, _) = CreateWorkspace(options: options);
        var session = await workspace.CreateNewAsync();
        session.ChangeText("title", "body");

        await session.DisposeAsync();
        await Task.Delay(50);

        Assert.Equal(0, drafts.SaveCalls);
        Assert.Equal(0, drafts.DeleteCalls);
    }

    private static (EditorWorkspaceService Workspace, FakeNoteRepository Notes, FakeDraftStore Drafts, FakeSaveCoordinator Saves)
        CreateWorkspace(EditorAutosaveOptions? options = null)
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var saves = new FakeSaveCoordinator();
        return (
            new EditorWorkspaceService(
                notes,
                saves,
                drafts,
                options ?? EditorTestData.DraftOnlyOptions(),
                new TestTimeProvider(EditorTestData.Timestamp)),
            notes,
            drafts,
            saves);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Condition was not reached in time.");
            }

            await Task.Delay(10);
        }
    }
}
