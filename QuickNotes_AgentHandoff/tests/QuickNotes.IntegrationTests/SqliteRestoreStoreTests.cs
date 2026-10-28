using QuickNotes.Application;
using QuickNotes.Domain;
using QuickNotes.Infrastructure.Sqlite;
using Xunit;

namespace QuickNotes.IntegrationTests;

public sealed class SqliteRestoreStoreTests
{
    [Fact]
    public async Task InspectAsync_ReturnsSchemaAndCounts()
    {
        using var database = await TestDatabase.CreateAsync();
        var tag = new Tag { Id = Guid.NewGuid(), Name = "restore-tag" };
        await database.Catalog.CreateTagAsync(tag);
        var active = TestData.NewNote();
        await database.Notes.CreateAsync(active, new[] { tag.Id });
        var archived = TestData.NewNote();
        await database.Notes.CreateAsync(archived, Array.Empty<Guid>());
        await database.Notes.SetLifecycleAsync(
            archived.Id,
            NoteLifecycle.Archived,
            1,
            archived.ModifiedAtUtc.AddMinutes(1));
        var trashed = TestData.NewNote();
        await database.Notes.CreateAsync(trashed, Array.Empty<Guid>());
        await database.Notes.SetLifecycleAsync(
            trashed.Id,
            NoteLifecycle.Trash,
            1,
            trashed.ModifiedAtUtc.AddMinutes(1));

        var info = await new SqliteRestoreStore().InspectAsync(database.DatabasePath);

        Assert.Equal(2, info.SchemaVersion);
        Assert.Equal(1, info.Counts.ActiveNotes);
        Assert.Equal(1, info.Counts.ArchivedNotes);
        Assert.Equal(1, info.Counts.TrashNotes);
        Assert.Equal(1, info.Counts.Categories);
        Assert.Equal(1, info.Counts.Tags);
        Assert.Equal(1, info.Counts.NoteTags);
    }

    [Fact]
    public async Task PrepareCurrentAsync_RejectsWalSidecars()
    {
        using var database = await TestDatabase.CreateAsync();
        await File.WriteAllTextAsync(database.DatabasePath + "-wal", string.Empty);

        var exception = await Assert.ThrowsAsync<RestoreException>(
            () => new SqliteRestoreStore().PrepareCurrentAsync(database.DatabasePath));

        Assert.Equal(RestoreErrorCodes.WalUnsupported, exception.ErrorCode);
    }

    [Fact]
    public async Task SwitchAndRollbackAsync_ReplacesAndRestoresDatabase()
    {
        using var current = await TestDatabase.CreateAsync();
        var noteA = TestData.NewNote();
        await current.Notes.CreateAsync(noteA, Array.Empty<Guid>());
        var directory = Path.GetDirectoryName(current.DatabasePath)!;
        var stagedPath = Path.Combine(directory, "staged.db");
        var rollbackPath = Path.Combine(directory, "rollback.db");
        var failedPath = Path.Combine(directory, "failed.db");
        await new SqliteSchema(new SqliteConnectionFactory(stagedPath)).InitializeAsync();
        var stagedNotes = new SqliteNoteRepository(new SqliteConnectionFactory(stagedPath));
        var noteB = TestData.NewNote();
        await stagedNotes.CreateAsync(noteB, Array.Empty<Guid>());
        var store = new SqliteRestoreStore();

        await store.PrepareCurrentAsync(current.DatabasePath);
        await store.SwitchAsync(stagedPath, current.DatabasePath, rollbackPath);
        var switched = new SqliteNoteRepository(
            new SqliteConnectionFactory(current.DatabasePath));
        Assert.NotNull(await switched.GetAsync(noteB.Id));
        Assert.Null(await switched.GetAsync(noteA.Id));

        await store.RollbackAsync(rollbackPath, current.DatabasePath, failedPath);
        var rolledBack = new SqliteNoteRepository(
            new SqliteConnectionFactory(current.DatabasePath));
        Assert.NotNull(await rolledBack.GetAsync(noteA.Id));
        Assert.Null(await rolledBack.GetAsync(noteB.Id));
    }

    [Fact]
    public async Task InspectAsync_WhenDatabaseMissing_ThrowsCurrentUnavailable()
    {
        using var temporary = new TemporaryDirectory();

        var exception = await Assert.ThrowsAsync<RestoreException>(
            () => new SqliteRestoreStore().InspectAsync(
                Path.Combine(temporary.RootPath, "missing.db")));

        Assert.Equal(RestoreErrorCodes.CurrentDatabaseUnavailable, exception.ErrorCode);
    }

    [Fact]
    public async Task InspectAsync_WhenDatabaseCorrupt_ThrowsInspectFailed()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.RootPath, "corrupt.db");
        await File.WriteAllTextAsync(path, "not a database");

        var exception = await Assert.ThrowsAsync<RestoreException>(
            () => new SqliteRestoreStore().InspectAsync(path));

        Assert.Equal(RestoreErrorCodes.InspectFailed, exception.ErrorCode);
    }

    [Fact]
    public async Task SwitchAsync_WhenCurrentHasSidecar_AbortsWithoutReplacing()
    {
        using var current = await TestDatabase.CreateAsync();
        var note = TestData.NewNote();
        await current.Notes.CreateAsync(note, Array.Empty<Guid>());
        var directory = Path.GetDirectoryName(current.DatabasePath)!;
        var stagedPath = Path.Combine(directory, "staged-sidecar.db");
        var rollbackPath = Path.Combine(directory, "rollback-sidecar.db");
        await new SqliteSchema(new SqliteConnectionFactory(stagedPath)).InitializeAsync();
        await File.WriteAllTextAsync(current.DatabasePath + "-journal", "journal");
        var store = new SqliteRestoreStore();

        var exception = await Assert.ThrowsAsync<RestoreException>(
            () => store.SwitchAsync(stagedPath, current.DatabasePath, rollbackPath));

        Assert.Equal(RestoreErrorCodes.PrepareFailed, exception.ErrorCode);
        Assert.NotNull(await current.Notes.GetAsync(note.Id));
    }

    [Fact]
    public async Task SwitchAsync_WhenStagedMissing_ThrowsSwitchFailed()
    {
        using var current = await TestDatabase.CreateAsync();
        using var temporary = new TemporaryDirectory();

        var exception = await Assert.ThrowsAsync<RestoreException>(
            () => new SqliteRestoreStore().SwitchAsync(
                Path.Combine(temporary.RootPath, "missing.db"),
                current.DatabasePath,
                Path.Combine(temporary.RootPath, "rollback.db")));

        Assert.Equal(RestoreErrorCodes.SwitchFailed, exception.ErrorCode);
    }
}
