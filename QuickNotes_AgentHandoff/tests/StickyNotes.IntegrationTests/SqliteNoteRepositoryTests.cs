using StickyNotes.Application;
using StickyNotes.Domain;
using Xunit;

namespace StickyNotes.IntegrationTests;

public sealed class SqliteNoteRepositoryTests
{
    [Fact]
    public async Task CreateAndGet_PersistsNoteCategoryAndTags()
    {
        using var database = await TestDatabase.CreateAsync();
        var tag = new Tag { Id = Guid.NewGuid(), Name = "work" };
        await database.Catalog.CreateTagAsync(tag);
        var note = TestData.NewNote();

        var created = await database.Notes.CreateAsync(note, new[] { tag.Id });
        var loaded = await database.Notes.GetAsync(note.Id);
        var tagIds = await database.Notes.GetTagIdsAsync(note.Id);

        Assert.Equal(note with { Version = 1 }, created);
        Assert.Equal(created, loaded);
        Assert.Equal(new[] { tag.Id }, tagIds);
    }

    [Fact]
    public async Task CloseAndReopen_PreservesNoteAndAssociations()
    {
        using var database = await TestDatabase.CreateAsync();
        var tag = new Tag { Id = Guid.NewGuid(), Name = "persistent" };
        await database.Catalog.CreateTagAsync(tag);
        var note = TestData.NewNote();
        await database.Notes.CreateAsync(note, new[] { tag.Id });

        database.Reopen();

        Assert.Equal(note, await database.Notes.GetAsync(note.Id));
        Assert.Equal(new[] { tag.Id }, await database.Notes.GetTagIdsAsync(note.Id));
    }

    [Fact]
    public async Task ForeignKeys_RejectNoteWithMissingCategory()
    {
        using var database = await TestDatabase.CreateAsync();
        var note = TestData.NewNote() with { CategoryId = Guid.NewGuid() };

        var exception = await Assert.ThrowsAsync<NoteConstraintException>(
            () => database.Notes.CreateAsync(note, Array.Empty<Guid>()));

        Assert.Equal("foreign_key", exception.ConstraintKind);
        Assert.Equal("create", exception.Operation);
        Assert.Equal(note.Id, exception.NoteId);
        Assert.Null(await database.Notes.GetAsync(note.Id));
    }

    [Fact]
    public async Task Update_ChangesMutableFieldsAndIncrementsVersion()
    {
        using var database = await TestDatabase.CreateAsync();
        var firstTag = new Tag { Id = Guid.NewGuid(), Name = "first" };
        var secondTag = new Tag { Id = Guid.NewGuid(), Name = "second" };
        await database.Catalog.CreateTagAsync(firstTag);
        await database.Catalog.CreateTagAsync(secondTag);
        var note = TestData.NewNote();
        await database.Notes.CreateAsync(note, new[] { firstTag.Id });
        var changed = note with
        {
            Title = null,
            Body = "changed",
            IsPinned = true,
            DisplayIntent = NoteDisplayIntent.Hidden,
            ModifiedAtUtc = note.ModifiedAtUtc.AddMinutes(1)
        };

        var updated = await database.Notes.UpdateAsync(changed, 1, new[] { secondTag.Id });

        Assert.Equal(2, updated.Version);
        Assert.Equal(changed with { Version = 2 }, await database.Notes.GetAsync(note.Id));
        Assert.Equal(new[] { secondTag.Id }, await database.Notes.GetTagIdsAsync(note.Id));
    }

    [Fact]
    public async Task Update_WithStaleVersion_DoesNotOverwriteNewerContent()
    {
        using var database = await TestDatabase.CreateAsync();
        var note = TestData.NewNote();
        await database.Notes.CreateAsync(note, Array.Empty<Guid>());
        await database.Notes.UpdateAsync(
            note with { Body = "newer", ModifiedAtUtc = note.ModifiedAtUtc.AddSeconds(1) },
            1,
            Array.Empty<Guid>());

        var exception = await Assert.ThrowsAsync<NoteConcurrencyException>(
            () => database.Notes.UpdateAsync(note with { Body = "stale" }, 1, Array.Empty<Guid>()));

        Assert.Equal(1, exception.ExpectedVersion);
        Assert.Equal(2, exception.ActualVersion);
        Assert.Equal("update", exception.Operation);
        var loaded = await database.Notes.GetAsync(note.Id);
        Assert.Equal("newer", loaded!.Body);
        Assert.Equal(2, loaded.Version);
    }

    [Fact]
    public async Task Update_WhenAssociationInsertFails_RollsBackContentVersionAndTags()
    {
        using var database = await TestDatabase.CreateAsync();
        var originalTag = new Tag { Id = Guid.NewGuid(), Name = "original" };
        await database.Catalog.CreateTagAsync(originalTag);
        var note = TestData.NewNote();
        await database.Notes.CreateAsync(note, new[] { originalTag.Id });

        var exception = await Assert.ThrowsAsync<NoteConstraintException>(
            () => database.Notes.UpdateAsync(
                note with { Body = "must roll back", ModifiedAtUtc = note.ModifiedAtUtc.AddSeconds(1) },
                1,
                new[] { Guid.NewGuid() }));

        Assert.Equal("foreign_key", exception.ConstraintKind);
        Assert.Equal("update", exception.Operation);
        Assert.Equal(note, await database.Notes.GetAsync(note.Id));
        Assert.Equal(new[] { originalTag.Id }, await database.Notes.GetTagIdsAsync(note.Id));
    }

    [Fact]
    public async Task SetLifecycle_ArchivesHidesAndIncrementsVersion()
    {
        using var database = await TestDatabase.CreateAsync();
        var note = TestData.NewNote();
        await database.Notes.CreateAsync(note, Array.Empty<Guid>());
        var modified = note.ModifiedAtUtc.AddMinutes(2);

        var archived = await database.Notes.SetLifecycleAsync(note.Id, NoteLifecycle.Archived, 1, modified);

        Assert.Equal(NoteLifecycle.Archived, archived.Lifecycle);
        Assert.Equal(NoteDisplayIntent.Hidden, archived.DisplayIntent);
        Assert.Equal(2, archived.Version);
        Assert.Equal(modified, archived.ModifiedAtUtc);
        Assert.Equal(archived, await database.Notes.GetAsync(note.Id));
    }

    [Fact]
    public async Task Update_HiddenActiveNote_DoesNotChangeLifecycle()
    {
        using var database = await TestDatabase.CreateAsync();
        var note = TestData.NewNote();
        await database.Notes.CreateAsync(note, Array.Empty<Guid>());

        await database.Notes.UpdateAsync(
            note with { DisplayIntent = NoteDisplayIntent.Hidden },
            1,
            Array.Empty<Guid>());

        var loaded = await database.Notes.GetAsync(note.Id);
        Assert.Equal(NoteLifecycle.Active, loaded!.Lifecycle);
        Assert.Equal(NoteDisplayIntent.Hidden, loaded.DisplayIntent);
    }
}
