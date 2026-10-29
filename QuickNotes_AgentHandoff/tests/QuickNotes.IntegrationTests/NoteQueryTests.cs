using QuickNotes.Application;
using QuickNotes.Domain;
using QuickNotes.Infrastructure.Sqlite;

namespace QuickNotes.IntegrationTests;

public sealed class NoteQueryTests
{
    [Fact]
    public async Task Query_FiltersByCategoryTagOrAndSearch()
    {
        using var database = await TestDatabase.CreateAsync();
        var category = new Category
        {
            Id = Guid.NewGuid(),
            Name = "工作",
            DefaultColor = "#E6E6FA",
            IsSystem = false
        };
        await database.Catalog.CreateCategoryAsync(category);
        var firstTag = new Tag { Id = Guid.NewGuid(), Name = "alpha" };
        var secondTag = new Tag { Id = Guid.NewGuid(), Name = "beta" };
        await database.Catalog.CreateTagAsync(firstTag);
        await database.Catalog.CreateTagAsync(secondTag);
        var first = TestData.NewNote() with
        {
            Title = "Alpha title",
            Body = "alpha body",
            CategoryId = category.Id,
            ModifiedAtUtc = TestData.NewNote().ModifiedAtUtc.AddMinutes(1)
        };
        var second = TestData.NewNote() with
        {
            Title = "Beta title",
            Body = "beta body",
            CategoryId = category.Id
        };
        var third = TestData.NewNote() with
        {
            Title = "Other title",
            Body = "other body"
        };
        await database.Notes.CreateAsync(first, new[] { firstTag.Id });
        await database.Notes.CreateAsync(second, new[] { secondTag.Id });
        await database.Notes.CreateAsync(third, Array.Empty<Guid>());

        var result = await database.Notes.QueryAsync(new NoteQuery
        {
            Lifecycle = NoteLifecycle.Active,
            CategoryId = category.Id,
            AnyTagIds = new[] { firstTag.Id },
            SearchText = "alpha"
        });

        Assert.Equal(new[] { first.Id }, result.Select(note => note.Id));
    }

    [Fact]
    public async Task Query_IncludeArchived_ExcludesTrash()
    {
        using var database = await TestDatabase.CreateAsync();
        var active = TestData.NewNote();
        var archived = TestData.NewNote();
        var trashed = TestData.NewNote();
        await database.Notes.CreateAsync(active, Array.Empty<Guid>());
        await database.Notes.CreateAsync(archived, Array.Empty<Guid>());
        await database.Notes.CreateAsync(trashed, Array.Empty<Guid>());
        await database.Notes.SetLifecycleAndDisplayAsync(
            archived.Id,
            NoteLifecycle.Archived,
            NoteDisplayIntent.Hidden,
            1,
            archived.ModifiedAtUtc.AddMinutes(1));
        await database.Notes.SetLifecycleAndDisplayAsync(
            trashed.Id,
            NoteLifecycle.Trash,
            NoteDisplayIntent.Hidden,
            1,
            trashed.ModifiedAtUtc.AddMinutes(2));

        var result = await database.Notes.QueryAsync(new NoteQuery
        {
            IncludeArchived = true
        });

        Assert.Equal(
            new[] { active.Id, archived.Id }.OrderBy(id => id),
            result.Select(note => note.Id).OrderBy(id => id));
        Assert.DoesNotContain(trashed.Id, result.Select(note => note.Id));
    }

    [Fact]
    public async Task Query_SearchEscapesLikeWildcards_AndMatchesTagNames()
    {
        using var database = await TestDatabase.CreateAsync();
        var tag = new Tag { Id = Guid.NewGuid(), Name = "needle-tag" };
        await database.Catalog.CreateTagAsync(tag);
        var tagged = TestData.NewNote() with { Title = "ordinary", Body = "body" };
        await database.Notes.CreateAsync(tagged, new[] { tag.Id });
        var literal = TestData.NewNote() with { Title = "100% done", Body = "body" };
        await database.Notes.CreateAsync(literal, Array.Empty<Guid>());

        var tagResult = await database.Notes.QueryAsync(new NoteQuery { SearchText = "needle-tag" });
        var literalResult = await database.Notes.QueryAsync(new NoteQuery { SearchText = "100%" });

        Assert.Equal(new[] { tagged.Id }, tagResult.Select(note => note.Id));
        Assert.Equal(new[] { literal.Id }, literalResult.Select(note => note.Id));
    }

    [Fact]
    public async Task Query_SortsByModifiedAndTitle()
    {
        using var database = await TestDatabase.CreateAsync();
        var first = TestData.NewNote() with
        {
            Title = "B",
            ModifiedAtUtc = new DateTimeOffset(2026, 10, 28, 10, 0, 0, TimeSpan.Zero)
        };
        var second = TestData.NewNote() with
        {
            Title = "A",
            ModifiedAtUtc = new DateTimeOffset(2026, 10, 28, 11, 0, 0, TimeSpan.Zero)
        };
        await database.Notes.CreateAsync(first, Array.Empty<Guid>());
        await database.Notes.CreateAsync(second, Array.Empty<Guid>());

        var byModified = await database.Notes.QueryAsync(new NoteQuery
        {
            Sort = NoteSortOrder.ModifiedDescending
        });
        var byTitle = await database.Notes.QueryAsync(new NoteQuery
        {
            Sort = NoteSortOrder.TitleAscending
        });

        Assert.Equal(new[] { second.Id, first.Id }, byModified.Select(note => note.Id));
        Assert.Equal(new[] { second.Id, first.Id }, byTitle.Select(note => note.Id));
    }
}
