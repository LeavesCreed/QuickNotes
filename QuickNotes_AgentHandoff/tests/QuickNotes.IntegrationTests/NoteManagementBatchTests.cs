using QuickNotes.Application;
using QuickNotes.Domain;

namespace QuickNotes.IntegrationTests;

public sealed class NoteManagementBatchTests
{
    [Fact]
    public async Task BatchMove_PartialFailure_ReportsEachItem()
    {
        using var database = await TestDatabase.CreateAsync();
        var category = new Category
        {
            Id = Guid.NewGuid(),
            Name = "工作",
            DefaultColor = "#E6E6FA"
        };
        await database.Catalog.CreateCategoryAsync(category);
        var first = TestData.NewNote();
        await database.Notes.CreateAsync(first, Array.Empty<Guid>());
        var service = CreateService(database);

        var result = await service.MoveToCategoryAsync(
            new[] { first.Id, Guid.NewGuid() },
            category.Id);

        Assert.False(result.AllSucceeded);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(NoteManagementItemStatus.Succeeded, result.Items[0].Status);
        Assert.Equal(NoteManagementItemStatus.NotFound, result.Items[1].Status);
        Assert.Equal(category.Id, (await database.Notes.GetAsync(first.Id))!.CategoryId);
    }

    [Fact]
    public async Task Undo_BatchMove_RestoresEachCategory_ButRespectsIndependentChanges()
    {
        using var database = await TestDatabase.CreateAsync();
        var category = new Category
        {
            Id = Guid.NewGuid(),
            Name = "工作",
            DefaultColor = "#E6E6FA"
        };
        await database.Catalog.CreateCategoryAsync(category);
        var first = TestData.NewNote();
        var second = TestData.NewNote();
        await database.Notes.CreateAsync(first, Array.Empty<Guid>());
        await database.Notes.CreateAsync(second, Array.Empty<Guid>());
        var service = CreateService(database);
        var undo = new ManagementUndoService(database.Notes, new DatabaseWriteGate());
        var token = await undo.CaptureAsync(
            ManagementUndoKind.Category,
            new[] { first.Id, second.Id });

        Assert.True((await service.MoveToCategoryAsync(
            new[] { first.Id, second.Id },
            category.Id)).AllSucceeded);
        var secondAfterMove = (await database.Notes.GetAsync(second.Id))!;
        await database.Notes.UpdateAsync(
            secondAfterMove with { Title = "independent" },
            secondAfterMove.Version,
            Array.Empty<Guid>());

        var undoResult = await undo.TryUndoAsync(token);

        Assert.False(undoResult.AllSucceeded);
        Assert.Equal(ManagementUndoItemStatus.Succeeded, undoResult.Items[0].Status);
        Assert.Equal(ManagementUndoItemStatus.Stale, undoResult.Items[1].Status);
        Assert.Equal(
            Category.UncategorizedId,
            (await database.Notes.GetAsync(first.Id))!.CategoryId);
        Assert.Equal(
            category.Id,
            (await database.Notes.GetAsync(second.Id))!.CategoryId);
    }

    [Fact]
    public async Task Undo_CopyUnmodified_MovesCopyToTrash()
    {
        using var database = await TestDatabase.CreateAsync();
        var source = TestData.NewNote() with { Body = "copy body" };
        await database.Notes.CreateAsync(source, Array.Empty<Guid>());
        var service = CreateService(database);
        var copy = await service.CreateCopyAsync(source.Id);
        var undo = new ManagementUndoService(database.Notes, new DatabaseWriteGate());
        var token = await undo.CaptureAsync(ManagementUndoKind.Copy, new[] { copy.Id });

        var result = await undo.TryUndoAsync(token);

        Assert.True(result.AllSucceeded);
        Assert.Equal(
            NoteLifecycle.Trash,
            (await database.Notes.GetAsync(copy.Id))!.Lifecycle);
    }

    private static NoteManagementService CreateService(TestDatabase database) =>
        new(
            database.Notes,
            database.Catalog,
            new DatabaseWriteGate(),
            TimeProvider.System);
}
