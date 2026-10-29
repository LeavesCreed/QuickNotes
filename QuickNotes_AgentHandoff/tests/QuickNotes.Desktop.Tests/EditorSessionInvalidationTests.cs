using QuickNotes.Application;
using QuickNotes.Desktop.Editing;

namespace QuickNotes.Desktop.Tests;

public sealed class EditorSessionInvalidationTests
{
    [Fact]
    public async Task Invalidate_RejectsLateEditsAndSaves()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var saves = new FakeSaveCoordinator();
        var note = EditorTestData.NewNote(version: 1);
        notes.Add(note);
        var workspace = new EditorWorkspaceService(
            notes,
            saves,
            drafts,
            EditorTestData.DraftOnlyOptions(),
            new TestTimeProvider(EditorTestData.Timestamp));
        var session = (await workspace.OpenAsync(note.Id))!;
        session.ChangeText("title", "before restore");

        session.Invalidate();

        Assert.True(session.IsInvalidated);
        Assert.Throws<ObjectDisposedException>(() => session.ChangeText("title", "late"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.SaveNowAsync());
        Assert.Equal("body", notes.Notes[note.Id].Body);
    }

    [Fact]
    public async Task Invalidate_CancelsPendingDraftWithoutDeletingExistingDraft()
    {
        var options = new EditorAutosaveOptions
        {
            DraftDebounce = TimeSpan.FromHours(1),
            DraftMaxDelay = TimeSpan.FromHours(2),
            SaveDebounce = TimeSpan.FromHours(1),
            SaveMaxDelay = TimeSpan.FromHours(2)
        };
        var drafts = new FakeDraftStore();
        var session = await new EditorWorkspaceService(
                new FakeNoteRepository(),
                new FakeSaveCoordinator(),
                drafts,
                options,
                new TestTimeProvider(EditorTestData.Timestamp))
            .CreateNewAsync();
        session.ChangeText("title", "body");
        await session.FlushDraftAsync();
        var draftId = session.DraftId;

        session.Invalidate();
        await Task.Delay(50);

        Assert.True(session.IsInvalidated);
        Assert.True(drafts.Drafts.ContainsKey(draftId));
        Assert.Equal(0, drafts.DeleteCalls);
    }
}
