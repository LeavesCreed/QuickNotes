using System.Globalization;
using QuickNotes.Application;
using QuickNotes.Domain;
using QuickNotes.Infrastructure.Sqlite;
using Xunit;

namespace QuickNotes.IntegrationTests;

public sealed class SqliteCatalogRepositoryTests
{
    [Fact]
    public async Task CreateCategoryAndTag_AdvanceChangeSequence()
    {
        using var database = await TestDatabase.CreateAsync();
        Assert.Equal(0L, await ReadSequenceAsync(database.Factory));

        await database.Catalog.CreateCategoryAsync(new Category
        {
            Id = Guid.NewGuid(),
            Name = "工作",
            DefaultColor = "#E6E6FA",
            IsSystem = false
        });
        Assert.Equal(1L, await ReadSequenceAsync(database.Factory));

        await database.Catalog.CreateTagAsync(new Tag
        {
            Id = Guid.NewGuid(),
            Name = "标签"
        });
        Assert.Equal(2L, await ReadSequenceAsync(database.Factory));
    }

    [Fact]
    public async Task UpdateCategory_PersistsNameAndColorAndAdvancesSequence()
    {
        using var database = await TestDatabase.CreateAsync();
        var category = await CreateCategoryAsync(database, "工作");
        var before = await ReadSequenceAsync(database.Factory);

        await database.Catalog.UpdateCategoryAsync(category.Id, "生活", "#FF0000");

        var reloaded = await database.Catalog.GetCategoryAsync(category.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("生活", reloaded.Name);
        Assert.Equal("#FF0000", reloaded.DefaultColor);
        Assert.False(reloaded.IsSystem);
        Assert.Equal(before + 1, await ReadSequenceAsync(database.Factory));
    }

    [Fact]
    public async Task UpdateCategory_Missing_ThrowsNoteNotFoundAndDoesNotAdvance()
    {
        using var database = await TestDatabase.CreateAsync();
        var before = await ReadSequenceAsync(database.Factory);

        var exception = await Assert.ThrowsAsync<NoteNotFoundException>(
            () => database.Catalog.UpdateCategoryAsync(Guid.NewGuid(), "生活", "#FF0000"));

        Assert.Equal("update_category", exception.Operation);
        Assert.Equal("note_not_found", exception.ErrorCode);
        Assert.Equal(before, await ReadSequenceAsync(database.Factory));
    }

    [Fact]
    public async Task UpdateCategory_DuplicateName_ThrowsTypedConstraintAndDoesNotAdvance()
    {
        using var database = await TestDatabase.CreateAsync();
        var first = await CreateCategoryAsync(database, "工作");
        var second = await CreateCategoryAsync(database, "生活");
        var before = await ReadSequenceAsync(database.Factory);

        var exception = await Assert.ThrowsAsync<NoteConstraintException>(
            () => database.Catalog.UpdateCategoryAsync(second.Id, "工作", second.DefaultColor));

        Assert.Equal("update_category", exception.Operation);
        Assert.Equal("unique", exception.ConstraintKind);
        Assert.Equal("生活", (await database.Catalog.GetCategoryAsync(second.Id))!.Name);
        Assert.Equal("工作", (await database.Catalog.GetCategoryAsync(first.Id))!.Name);
        Assert.Equal(before, await ReadSequenceAsync(database.Factory));
    }

    [Fact]
    public async Task DeleteCategory_ReassignsNotesAndAdvancesSequence()
    {
        using var database = await TestDatabase.CreateAsync();
        var category = await CreateCategoryAsync(database, "工作");
        var note = TestData.NewNote() with { CategoryId = category.Id };
        await database.Notes.CreateAsync(note, Array.Empty<Guid>());
        var before = await ReadSequenceAsync(database.Factory);

        await database.Catalog.DeleteCategoryAsync(category.Id, Category.UncategorizedId);

        Assert.Null(await database.Catalog.GetCategoryAsync(category.Id));
        var reloaded = await database.Notes.GetAsync(note.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(Category.UncategorizedId, reloaded.CategoryId);
        Assert.Equal(2, reloaded.Version);
        Assert.NotEqual(note.ModifiedAtUtc, reloaded.ModifiedAtUtc);
        Assert.Equal(before + 1, await ReadSequenceAsync(database.Factory));
    }

    [Fact]
    public async Task DeleteCategory_System_ThrowsInvalidOperationAndKeepsCategory()
    {
        using var database = await TestDatabase.CreateAsync();
        var before = await ReadSequenceAsync(database.Factory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => database.Catalog.DeleteCategoryAsync(
                Category.UncategorizedId,
                Category.UncategorizedId));

        Assert.Contains("system", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(await database.Catalog.GetCategoryAsync(Category.UncategorizedId));
        Assert.Equal(before, await ReadSequenceAsync(database.Factory));
    }

    [Fact]
    public async Task DeleteCategory_Missing_ThrowsNoteNotFoundAndDoesNotAdvance()
    {
        using var database = await TestDatabase.CreateAsync();
        var before = await ReadSequenceAsync(database.Factory);

        var exception = await Assert.ThrowsAsync<NoteNotFoundException>(
            () => database.Catalog.DeleteCategoryAsync(Guid.NewGuid(), Category.UncategorizedId));

        Assert.Equal("delete_category", exception.Operation);
        Assert.Equal("note_not_found", exception.ErrorCode);
        Assert.Equal(before, await ReadSequenceAsync(database.Factory));
    }

    [Fact]
    public async Task DeleteCategory_MissingReassignTarget_RollsBackAndDoesNotAdvance()
    {
        using var database = await TestDatabase.CreateAsync();
        var category = await CreateCategoryAsync(database, "工作");
        var note = TestData.NewNote() with { CategoryId = category.Id };
        await database.Notes.CreateAsync(note, Array.Empty<Guid>());
        var before = await ReadSequenceAsync(database.Factory);

        var exception = await Assert.ThrowsAsync<NoteConstraintException>(
            () => database.Catalog.DeleteCategoryAsync(category.Id, Guid.NewGuid()));

        Assert.Equal("delete_category", exception.Operation);
        Assert.Equal("foreign_key", exception.ConstraintKind);
        Assert.NotNull(await database.Catalog.GetCategoryAsync(category.Id));
        var reloaded = await database.Notes.GetAsync(note.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(category.Id, reloaded.CategoryId);
        Assert.Equal(1, reloaded.Version);
        Assert.Equal(note.ModifiedAtUtc, reloaded.ModifiedAtUtc);
        Assert.Equal(before, await ReadSequenceAsync(database.Factory));
    }

    [Fact]
    public async Task UpdateTag_RenamePersistsAndAdvancesSequence()
    {
        using var database = await TestDatabase.CreateAsync();
        var tag = new Tag { Id = Guid.NewGuid(), Name = "alpha" };
        await database.Catalog.CreateTagAsync(tag);
        var before = await ReadSequenceAsync(database.Factory);

        await database.Catalog.UpdateTagAsync(tag.Id, "beta");

        Assert.Equal("beta", (await database.Catalog.GetTagAsync(tag.Id))!.Name);
        Assert.Equal(before + 1, await ReadSequenceAsync(database.Factory));
    }

    [Fact]
    public async Task UpdateTag_UniqueConflict_ThrowsTypedConstraintAndDoesNotAdvance()
    {
        using var database = await TestDatabase.CreateAsync();
        var first = new Tag { Id = Guid.NewGuid(), Name = "alpha" };
        var second = new Tag { Id = Guid.NewGuid(), Name = "beta" };
        await database.Catalog.CreateTagAsync(first);
        await database.Catalog.CreateTagAsync(second);
        var before = await ReadSequenceAsync(database.Factory);

        var exception = await Assert.ThrowsAsync<NoteConstraintException>(
            () => database.Catalog.UpdateTagAsync(second.Id, "ALPHA"));

        Assert.Equal("update_tag", exception.Operation);
        Assert.Equal("unique", exception.ConstraintKind);
        Assert.Equal("beta", (await database.Catalog.GetTagAsync(second.Id))!.Name);
        Assert.Equal(before, await ReadSequenceAsync(database.Factory));
    }

    [Fact]
    public async Task UpdateTag_Missing_ThrowsNoteNotFoundAndDoesNotAdvance()
    {
        using var database = await TestDatabase.CreateAsync();
        var before = await ReadSequenceAsync(database.Factory);

        var exception = await Assert.ThrowsAsync<NoteNotFoundException>(
            () => database.Catalog.UpdateTagAsync(Guid.NewGuid(), "beta"));

        Assert.Equal("update_tag", exception.Operation);
        Assert.Equal("note_not_found", exception.ErrorCode);
        Assert.Equal(before, await ReadSequenceAsync(database.Factory));
    }

    [Fact]
    public async Task DeleteTag_RemovesNoteLinksAndAdvancesSequence()
    {
        using var database = await TestDatabase.CreateAsync();
        var tag = new Tag { Id = Guid.NewGuid(), Name = "alpha" };
        await database.Catalog.CreateTagAsync(tag);
        var note = TestData.NewNote();
        await database.Notes.CreateAsync(note, new[] { tag.Id });
        var before = await ReadSequenceAsync(database.Factory);

        await database.Catalog.DeleteTagAsync(tag.Id);

        Assert.Null(await database.Catalog.GetTagAsync(tag.Id));
        Assert.Empty(await database.Notes.GetTagIdsAsync(note.Id));
        Assert.NotNull(await database.Notes.GetAsync(note.Id));
        Assert.Equal(before + 1, await ReadSequenceAsync(database.Factory));
    }

    [Fact]
    public async Task DeleteTag_Missing_ThrowsNoteNotFoundAndDoesNotAdvance()
    {
        using var database = await TestDatabase.CreateAsync();
        var before = await ReadSequenceAsync(database.Factory);

        var exception = await Assert.ThrowsAsync<NoteNotFoundException>(
            () => database.Catalog.DeleteTagAsync(Guid.NewGuid()));

        Assert.Equal("delete_tag", exception.Operation);
        Assert.Equal("note_not_found", exception.ErrorCode);
        Assert.Equal(before, await ReadSequenceAsync(database.Factory));
    }

    private static async Task<Category> CreateCategoryAsync(TestDatabase database, string name)
    {
        var category = new Category
        {
            Id = Guid.NewGuid(),
            Name = name,
            DefaultColor = "#E6E6FA",
            IsSystem = false
        };
        await database.Catalog.CreateCategoryAsync(category);
        return category;
    }

    private static async Task<long> ReadSequenceAsync(SqliteConnectionFactory factory)
    {
        await using var connection = await factory.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT change_sequence FROM database_meta WHERE id = 1;";
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            CultureInfo.InvariantCulture);
    }
}
