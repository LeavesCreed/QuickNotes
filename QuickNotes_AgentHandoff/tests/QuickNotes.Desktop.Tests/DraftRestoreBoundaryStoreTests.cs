using QuickNotes.Application;
using QuickNotes.Desktop.Editing;
using QuickNotes.Desktop.Paths;
using QuickNotes.Infrastructure.Drafts;

namespace QuickNotes.Desktop.Tests;

public sealed class DraftRestoreBoundaryStoreTests
{
    [Fact]
    public async Task WriteRead_RoundTripsBoundaryAtomically()
    {
        using var temporary = new TestTemporaryDirectory();
        var path = Path.Combine(temporary.RootPath, "draft-restore-boundary.json");
        var store = new DraftRestoreBoundaryStore(path);
        var draftId = Guid.NewGuid();
        var boundary = new DraftRestoreBoundary
        {
            FormatVersion = DraftRestoreBoundary.CurrentFormatVersion,
            RestoreId = Guid.NewGuid(),
            RestoredAtUtc = DateTimeOffset.UtcNow,
            DraftIds = new[] { draftId }
        };

        await store.WriteAsync(boundary);
        var read = await store.ReadAsync();

        Assert.NotNull(read);
        Assert.Equal(boundary.RestoreId, read.RestoreId);
        Assert.Contains(draftId, read.DraftIds);
        Assert.Empty(Directory.GetFiles(temporary.RootPath, "*.tmp"));
    }

    [Fact]
    public async Task Read_CorruptOrUnsupported_ReturnsNull()
    {
        using var temporary = new TestTemporaryDirectory();
        var path = Path.Combine(temporary.RootPath, "draft-restore-boundary.json");
        var store = new DraftRestoreBoundaryStore(path);
        await File.WriteAllTextAsync(path, "{");

        Assert.Null(await store.ReadAsync());

        await File.WriteAllTextAsync(path, """{"formatVersion":99}""");
        Assert.Null(await store.ReadAsync());
    }

    [Fact]
    public async Task DraftRecovery_MarksOnlyIdsInBoundary()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(temporary.RootPath);
        var notes = new FakeNoteRepository();
        var drafts = new FileNoteDraftStore(paths.DraftDirectory);
        var workspace = new EditorWorkspaceService(
            notes,
            new FakeSaveCoordinator(),
            drafts,
            EditorTestData.DraftOnlyOptions(),
            new TestTimeProvider(EditorTestData.Timestamp));
        var first = NewDraft();
        var second = NewDraft();
        await drafts.SaveAsync(first);
        await drafts.SaveAsync(second);
        var boundaryStore = new DraftRestoreBoundaryStore(paths.DraftRestoreBoundaryPath);
        await boundaryStore.WriteAsync(new DraftRestoreBoundary
        {
            FormatVersion = DraftRestoreBoundary.CurrentFormatVersion,
            RestoreId = Guid.NewGuid(),
            RestoredAtUtc = EditorTestData.Timestamp,
            DraftIds = new[] { first.DraftId }
        });
        var recovery = new DraftRecoveryService(paths, notes, drafts, workspace, boundaryStore);

        var inventory = await recovery.ListAsync();

        Assert.Equal(2, inventory.Items.Count);
        Assert.True(inventory.Items.Single(item => item.Draft.DraftId == first.DraftId).IsPreRestore);
        Assert.False(inventory.Items.Single(item => item.Draft.DraftId == second.DraftId).IsPreRestore);
    }

    private static NoteDraft NewDraft() => new()
    {
        DraftId = Guid.NewGuid(),
        NoteId = Guid.NewGuid(),
        BaseVersion = 0,
        Title = "title",
        Body = "body",
        CategoryId = QuickNotes.Domain.Category.UncategorizedId,
        TagIds = Array.Empty<Guid>(),
        CapturedAtUtc = EditorTestData.Timestamp
    };
}
