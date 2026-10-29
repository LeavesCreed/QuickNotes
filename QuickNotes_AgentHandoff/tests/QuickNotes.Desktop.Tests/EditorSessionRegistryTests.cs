using QuickNotes.Desktop.Editing;

namespace QuickNotes.Desktop.Tests;

public sealed class EditorSessionRegistryTests
{
    [Fact]
    public async Task GetOrOpen_SameNote_ReturnsSharedSession_AndDisposesAfterLastRelease()
    {
        var (workspace, notes, _) = CreateWorkspace();
        var note = EditorTestData.NewNote();
        notes.Add(note);
        await using var registry = new EditorSessionRegistry(workspace);

        var first = await registry.GetOrOpenAsync(note.Id);
        var second = await registry.GetOrOpenAsync(note.Id);

        Assert.Same(first, second);
        Assert.Equal(1, registry.Count);
        await registry.ReleaseAsync(note.Id);
        Assert.False(first.IsDisposed);
        await registry.ReleaseAsync(note.Id);
        Assert.True(first.IsDisposed);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task Adopt_WhenSessionAlreadyOpen_ReturnsExistingAndDisposesIncoming()
    {
        var (workspace, notes, _) = CreateWorkspace();
        var note = EditorTestData.NewNote();
        notes.Add(note);
        await using var registry = new EditorSessionRegistry(workspace);
        var existing = await registry.GetOrOpenAsync(note.Id);
        var incoming = await workspace.OpenAsync(note.Id);

        var adopted = await registry.AdoptAsync(incoming!);

        Assert.Same(existing, adopted);
        Assert.True(incoming!.IsDisposed);
        await registry.ReleaseAsync(note.Id);
        await registry.ReleaseAsync(note.Id);
        Assert.True(existing.IsDisposed);
    }

    [Fact]
    public async Task CreateNew_IsRegisteredAndCanBeReleased()
    {
        var (workspace, _, _) = CreateWorkspace();
        await using var registry = new EditorSessionRegistry(workspace);

        var session = await registry.CreateNewAsync();
        Assert.True(registry.Contains(session.NoteId));
        Assert.False(session.IsDisposed);

        await registry.ReleaseAsync(session.NoteId);
        Assert.True(session.IsDisposed);
    }

    [Fact]
    public async Task DisposeRegistry_DisposesAllSharedSessions()
    {
        var (workspace, notes, _) = CreateWorkspace();
        var note = EditorTestData.NewNote();
        notes.Add(note);
        var registry = new EditorSessionRegistry(workspace);
        var session = await registry.GetOrOpenAsync(note.Id);
        await registry.GetOrOpenAsync(note.Id);

        await registry.DisposeAsync();

        Assert.True(session.IsDisposed);
        Assert.Equal(0, registry.Count);
    }

    private static (EditorWorkspaceService Workspace, FakeNoteRepository Notes, FakeSaveCoordinator Saves)
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
            saves);
    }
}
