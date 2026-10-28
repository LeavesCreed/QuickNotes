using QuickNotes.Application;
using QuickNotes.Desktop.Editing;
using QuickNotes.Desktop.Paths;
using QuickNotes.Infrastructure.Drafts;

namespace QuickNotes.Desktop.Tests;

public sealed class EditorWorkspaceServiceTests
{
    [Fact]
    public async Task ListActive_ReturnsActiveNotesOnly()
    {
        var notes = new FakeNoteRepository();
        var active = EditorTestData.NewNote();
        var archived = EditorTestData.NewNote() with { Lifecycle = QuickNotes.Domain.NoteLifecycle.Archived };
        notes.Add(active);
        notes.Add(archived);
        var workspace = CreateWorkspace(notes, new FakeDraftStore());

        var result = await workspace.Service.ListActiveAsync();

        Assert.Equal(new[] { active.Id }, result.Select(note => note.Id));
    }

    [Fact]
    public async Task CreateNew_UsesEmptyContentAndStableIdentity()
    {
        var notes = new FakeNoteRepository();
        var drafts = new FakeDraftStore();
        var workspace = CreateWorkspace(notes, drafts);

        var session = await workspace.Service.CreateNewAsync();

        Assert.True(session.IsNew);
        Assert.Null(session.Title);
        Assert.Equal(string.Empty, session.Body);
        Assert.NotEqual(Guid.Empty, session.NoteId);
        Assert.Empty(notes.Notes);
    }

    [Fact]
    public async Task Open_LoadsNote()
    {
        var notes = new FakeNoteRepository();
        var note = EditorTestData.NewNote(version: 4, body: "stored");
        notes.Add(note);
        var workspace = CreateWorkspace(notes, new FakeDraftStore());

        var session = await workspace.Service.OpenAsync(note.Id);

        Assert.NotNull(session);
        Assert.False(session!.IsNew);
        Assert.Equal("stored", session.Body);
        Assert.Equal(4, session.Status.BaseVersion);
    }

    [Fact]
    public async Task DraftRecovery_ListsValidDraftsWithExistingNoteFlag()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(temporary.RootPath);
        var notes = new FakeNoteRepository();
        var note = EditorTestData.NewNote();
        notes.Add(note);
        var drafts = new FileNoteDraftStore(paths.DraftDirectory);
        var workspace = CreateWorkspace(notes, drafts, paths);
        await drafts.SaveAsync(CreateDraft(note.Id, note.Version));

        var inventory = await workspace.Recovery.ListAsync();

        var item = Assert.Single(inventory.Items);
        Assert.True(item.NoteExists);
        Assert.Empty(inventory.Issues);
    }

    [Fact]
    public async Task DraftRecovery_CorruptDraft_ReturnsIssueAndOtherDrafts()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(temporary.RootPath);
        var notes = new FakeNoteRepository();
        var note = EditorTestData.NewNote();
        notes.Add(note);
        Directory.CreateDirectory(paths.DraftDirectory);
        var drafts = new FileNoteDraftStore(paths.DraftDirectory);
        var workspace = CreateWorkspace(notes, drafts, paths);
        var validDraft = CreateDraft(note.Id, note.Version);
        await drafts.SaveAsync(validDraft);
        await File.WriteAllTextAsync(
            Path.Combine(paths.DraftDirectory, $"{Guid.NewGuid():D}.json"),
            "{");

        var inventory = await workspace.Recovery.ListAsync();

        Assert.Single(inventory.Items);
        var issue = Assert.Single(inventory.Issues);
        Assert.Equal("draft_corrupt", issue.ErrorCode);
        Assert.True(File.Exists(Path.Combine(paths.DraftDirectory, $"{validDraft.DraftId:D}.json")));
    }

    [Fact]
    public async Task DraftRecovery_NewNoteDraft_OpensNewSessionWithStableIds()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(temporary.RootPath);
        var notes = new FakeNoteRepository();
        var drafts = new FileNoteDraftStore(paths.DraftDirectory);
        var workspace = CreateWorkspace(notes, drafts, paths);
        var draft = CreateDraft(Guid.NewGuid(), 0, body: "new draft");
        await drafts.SaveAsync(draft);
        var inventory = await workspace.Recovery.ListAsync();
        var item = Assert.Single(inventory.Items);
        Assert.False(item.NoteExists);

        var session = await workspace.Recovery.OpenAsync(item.Draft);

        Assert.True(session.IsNew);
        Assert.Equal(item.Draft.NoteId, session.NoteId);
        Assert.Equal(item.Draft.DraftId, session.DraftId);
        Assert.Equal("new draft", session.Body);
        Assert.True(session.Status.HasPendingChanges);
    }

    [Fact]
    public async Task DraftRecovery_ExistingNoteDraft_PreservesBaseVersion()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(temporary.RootPath);
        var notes = new FakeNoteRepository();
        var note = EditorTestData.NewNote(version: 5);
        notes.Add(note);
        var drafts = new FileNoteDraftStore(paths.DraftDirectory);
        var workspace = CreateWorkspace(notes, drafts, paths);
        var draft = CreateDraft(note.Id, note.Version, body: "draft body");
        await drafts.SaveAsync(draft);
        var inventory = await workspace.Recovery.ListAsync();
        var item = Assert.Single(inventory.Items);

        var session = await workspace.Recovery.OpenAsync(item.Draft);

        Assert.False(session.IsNew);
        Assert.Equal(5, session.Status.BaseVersion);
        Assert.Equal("draft body", session.Body);
    }

    [Fact]
    public async Task DiscardDraft_DeletesOnlyRequestedDraft()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(temporary.RootPath);
        var notes = new FakeNoteRepository();
        var drafts = new FileNoteDraftStore(paths.DraftDirectory);
        var workspace = CreateWorkspace(notes, drafts, paths);
        var first = CreateDraft(Guid.NewGuid(), 0, body: "first");
        var second = CreateDraft(Guid.NewGuid(), 0, body: "second");
        await drafts.SaveAsync(first);
        await drafts.SaveAsync(second);

        await workspace.Recovery.DiscardAsync(first.DraftId);

        Assert.Null(await drafts.GetAsync(first.DraftId));
        Assert.NotNull(await drafts.GetAsync(second.DraftId));
    }

    private static (EditorWorkspaceService Service, DraftRecoveryService Recovery) CreateWorkspace(
        FakeNoteRepository notes,
        INoteDraftStore drafts,
        DesktopPaths? paths = null)
    {
        var saves = new FakeSaveCoordinator();
        var service = new EditorWorkspaceService(
            notes,
            saves,
            drafts,
            EditorTestData.DraftOnlyOptions(),
            new TestTimeProvider(EditorTestData.Timestamp));
        var recovery = new DraftRecoveryService(
            paths ?? DesktopPaths.Create(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))),
            notes,
            drafts,
            service);
        return (service, recovery);
    }

    private static NoteDraft CreateDraft(
        Guid noteId,
        long baseVersion,
        Guid? draftId = null,
        string body = "draft") => new()
    {
        DraftId = draftId ?? Guid.NewGuid(),
        NoteId = noteId,
        BaseVersion = baseVersion,
        Title = "draft title",
        Body = body,
        CategoryId = QuickNotes.Domain.Category.UncategorizedId,
        TagIds = Array.Empty<Guid>(),
        CapturedAtUtc = EditorTestData.Timestamp
    };
}
