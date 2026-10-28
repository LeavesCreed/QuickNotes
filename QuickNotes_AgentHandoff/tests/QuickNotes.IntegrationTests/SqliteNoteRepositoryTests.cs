using QuickNotes.Application;
using QuickNotes.Domain;
using QuickNotes.Infrastructure.Sqlite;
using Xunit;

namespace QuickNotes.IntegrationTests;

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
    public async Task List_FiltersLifecycleAndOrdersByModifiedDesc()
    {
        using var database = await TestDatabase.CreateAsync();
        var first = TestData.NewNote();
        var second = TestData.NewNote() with
        {
            ModifiedAtUtc = first.ModifiedAtUtc.AddMinutes(1)
        };
        await database.Notes.CreateAsync(first, Array.Empty<Guid>());
        await database.Notes.CreateAsync(second, Array.Empty<Guid>());

        var active = await database.Notes.ListAsync(NoteLifecycle.Active);

        Assert.Equal(new[] { second.Id, first.Id }, active.Select(note => note.Id));
    }

    [Fact]
    public async Task List_EmptyDatabase_ReturnsEmpty()
    {
        using var database = await TestDatabase.CreateAsync();

        var active = await database.Notes.ListAsync(NoteLifecycle.Active);

        Assert.Empty(active);
    }

    [Fact]
    public async Task List_DoesNotReturnArchivedOrTrash()
    {
        using var database = await TestDatabase.CreateAsync();
        var active = TestData.NewNote();
        var archived = TestData.NewNote();
        var trashed = TestData.NewNote();
        await database.Notes.CreateAsync(active, Array.Empty<Guid>());
        await database.Notes.CreateAsync(archived, Array.Empty<Guid>());
        await database.Notes.CreateAsync(trashed, Array.Empty<Guid>());
        await database.Notes.SetLifecycleAsync(
            archived.Id,
            NoteLifecycle.Archived,
            1,
            archived.ModifiedAtUtc.AddMinutes(1));
        await database.Notes.SetLifecycleAsync(
            trashed.Id,
            NoteLifecycle.Trash,
            1,
            trashed.ModifiedAtUtc.AddMinutes(2));

        var result = await database.Notes.ListAsync(NoteLifecycle.Active);

        Assert.Equal(new[] { active.Id }, result.Select(note => note.Id));
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

    [Fact]
    public async Task Note_AppearanceAndLayout_RoundTrips()
    {
        using var database = new TestDatabase();
        await new SqliteSchema(database.Factory).InitializeAsync();
        var repository = new SqliteNoteRepository(database.Factory);
        var now = DateTimeOffset.UtcNow;
        var note = new Note
        {
            Id = Guid.NewGuid(),
            Title = "title",
            Body = "body",
            CreatedAtUtc = now,
            ModifiedAtUtc = now,
            CategoryId = Category.UncategorizedId,
            ColorMode = NoteColorMode.Manual,
            ColorHex = "#FFCC00",
            FontSize = 20,
            WindowLayout = new NoteWindowLayout
            {
                Left = -120,
                Top = 80,
                Width = 360,
                Height = 280
            }
        };

        await repository.CreateAsync(note, Array.Empty<Guid>());
        var loaded = await repository.GetAsync(note.Id);

        Assert.Equal(NoteColorMode.Manual, loaded!.ColorMode);
        Assert.Equal("#FFCC00", loaded.ColorHex);
        Assert.Equal(20, loaded.FontSize);
        Assert.Equal(note.WindowLayout, loaded.WindowLayout);
    }

    [Fact]
    public async Task CreateAndGet_DefaultAppearanceIsFollowCategoryWithoutOverrides()
    {
        using var database = await TestDatabase.CreateAsync();
        var note = TestData.NewNote();

        var created = await database.Notes.CreateAsync(note, Array.Empty<Guid>());
        var loaded = await database.Notes.GetAsync(note.Id);

        Assert.Equal(NoteColorMode.FollowCategory, created.ColorMode);
        Assert.Null(created.ColorHex);
        Assert.Null(created.FontSize);
        Assert.Null(created.WindowLayout);
        Assert.Equal(created, loaded);
    }

    [Fact]
    public async Task Update_PersistsAppearanceAndLayout()
    {
        using var database = await TestDatabase.CreateAsync();
        var note = TestData.NewNote();
        await database.Notes.CreateAsync(note, Array.Empty<Guid>());
        var changed = note with
        {
            ColorMode = NoteColorMode.Manual,
            ColorHex = "#112233",
            FontSize = 30,
            WindowLayout = new NoteWindowLayout
            {
                Left = 10,
                Top = 20,
                Width = 400,
                Height = 300
            },
            ModifiedAtUtc = note.ModifiedAtUtc.AddMinutes(1)
        };

        var updated = await database.Notes.UpdateAsync(changed, 1, Array.Empty<Guid>());

        Assert.Equal(NoteColorMode.Manual, updated.ColorMode);
        Assert.Equal("#112233", updated.ColorHex);
        Assert.Equal(30, updated.FontSize);
        Assert.Equal(changed.WindowLayout, updated.WindowLayout);
        Assert.Equal(updated, await database.Notes.GetAsync(note.Id));
    }

    [Fact]
    public async Task Create_WithInvalidAppearance_ThrowsBeforeWriting()
    {
        using var database = await TestDatabase.CreateAsync();
        var repository = database.Notes;
        var note = TestData.NewNote();

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.CreateAsync(
                note with { ColorMode = NoteColorMode.Manual, ColorHex = "not-a-color" },
                Array.Empty<Guid>()));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repository.CreateAsync(
                note with { FontSize = NoteRules.MaxFontSize + 1 },
                Array.Empty<Guid>()));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repository.CreateAsync(
                note with
                {
                    WindowLayout = new NoteWindowLayout
                    {
                        Left = 0,
                        Top = 0,
                        Width = 0,
                        Height = 100
                    }
                },
                Array.Empty<Guid>()));

        Assert.Null(await repository.GetAsync(note.Id));
    }

    [Fact]
    public async Task NoteWrites_AdvanceChangeSequenceAndFailedWriteDoesNot()
    {
        using var database = await TestDatabase.CreateAsync();
        var note = TestData.NewNote();
        Assert.Equal(0, await ReadChangeSequenceAsync(database.Factory));

        await database.Notes.CreateAsync(note, Array.Empty<Guid>());
        Assert.Equal(1, await ReadChangeSequenceAsync(database.Factory));

        await database.Notes.UpdateAsync(
            note with
            {
                Body = "updated",
                ModifiedAtUtc = note.ModifiedAtUtc.AddMinutes(1)
            },
            1,
            Array.Empty<Guid>());
        Assert.Equal(2, await ReadChangeSequenceAsync(database.Factory));

        await database.Notes.SetLifecycleAsync(
            note.Id,
            NoteLifecycle.Archived,
            2,
            note.ModifiedAtUtc.AddMinutes(2));
        Assert.Equal(3, await ReadChangeSequenceAsync(database.Factory));

        await Assert.ThrowsAsync<NoteConstraintException>(
            () => database.Notes.CreateAsync(
                TestData.NewNote() with { CategoryId = Guid.NewGuid() },
                Array.Empty<Guid>()));
        Assert.Equal(3, await ReadChangeSequenceAsync(database.Factory));
    }

    private static async Task<long> ReadChangeSequenceAsync(SqliteConnectionFactory factory)
    {
        await using var connection = await factory.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT change_sequence FROM database_meta WHERE id = 1;";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
