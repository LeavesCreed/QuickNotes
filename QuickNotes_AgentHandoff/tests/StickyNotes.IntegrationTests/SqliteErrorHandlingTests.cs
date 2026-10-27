using Microsoft.Data.Sqlite;
using StickyNotes.Application;
using StickyNotes.Domain;
using StickyNotes.Infrastructure.Sqlite;
using Xunit;

namespace StickyNotes.IntegrationTests;

public sealed class SqliteErrorHandlingTests
{
    [Fact]
    public async Task Update_WhenNoteMissing_ThrowsNoteNotFound()
    {
        using var database = await TestDatabase.CreateAsync();
        var note = TestData.NewNote();

        var exception = await Assert.ThrowsAsync<NoteNotFoundException>(
            () => database.Notes.UpdateAsync(note, 1, Array.Empty<Guid>()));

        Assert.Equal("update", exception.Operation);
        Assert.Equal(note.Id, exception.NoteId);
    }

    [Fact]
    public async Task SetLifecycle_WhenNoteMissing_ThrowsNoteNotFound()
    {
        using var database = await TestDatabase.CreateAsync();
        var id = Guid.NewGuid();

        var exception = await Assert.ThrowsAsync<NoteNotFoundException>(
            () => database.Notes.SetLifecycleAsync(id, NoteLifecycle.Archived, 1, DateTimeOffset.UtcNow));

        Assert.Equal("set_lifecycle", exception.Operation);
        Assert.Equal(id, exception.NoteId);
    }

    [Fact]
    public async Task Update_ArchivedNoteWithVisibleIntent_ThrowsCheckConstraint()
    {
        using var database = await TestDatabase.CreateAsync();
        var note = TestData.NewNote();
        await database.Notes.CreateAsync(note, Array.Empty<Guid>());
        var archivedAt = note.ModifiedAtUtc.AddMinutes(1);
        await database.Notes.SetLifecycleAsync(note.Id, NoteLifecycle.Archived, 1, archivedAt);

        var exception = await Assert.ThrowsAsync<NoteConstraintException>(
            () => database.Notes.UpdateAsync(
                note with { Body = "visible on archived", ModifiedAtUtc = archivedAt.AddMinutes(1) },
                2,
                Array.Empty<Guid>()));

        Assert.Equal("check", exception.ConstraintKind);
        Assert.Equal(NoteLifecycle.Archived, (await database.Notes.GetAsync(note.Id))!.Lifecycle);
    }

    [Fact]
    public async Task Create_ReturnsAuthoritativeRowWithNormalizedUtc()
    {
        using var database = await TestDatabase.CreateAsync();
        var offset = TimeSpan.FromHours(8);
        var timestamp = new DateTimeOffset(2026, 10, 27, 20, 30, 0, offset);
        var note = TestData.NewNote() with
        {
            CreatedAtUtc = timestamp,
            ModifiedAtUtc = timestamp.AddMinutes(5)
        };

        var created = await database.Notes.CreateAsync(note, Array.Empty<Guid>());

        Assert.Equal(TimeSpan.Zero, created.CreatedAtUtc.Offset);
        Assert.Equal(timestamp.UtcDateTime, created.CreatedAtUtc.UtcDateTime);
        Assert.Equal(TimeSpan.Zero, created.ModifiedAtUtc.Offset);
        Assert.Equal(created, await database.Notes.GetAsync(note.Id));
    }

    [Fact]
    public async Task Connection_UsesDefaultBusyTimeoutOf250Milliseconds()
    {
        using var database = new TemporaryDatabase();
        await new SqliteSchema(new SqliteConnectionFactory(database.DatabasePath)).InitializeAsync();
        var factory = new SqliteConnectionFactory(database.DatabasePath);

        await using var connection = await factory.OpenAsync();

        Assert.Equal(250, await ScalarIntAsync(connection, "PRAGMA busy_timeout;"));
    }

    [Fact]
    public async Task Connection_UsesConfiguredBusyTimeout()
    {
        using var database = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(database.DatabasePath, busyTimeoutMilliseconds: 1234);
        await new SqliteSchema(factory).InitializeAsync();

        await using var connection = await factory.OpenAsync();

        Assert.Equal(1234, await ScalarIntAsync(connection, "PRAGMA busy_timeout;"));
    }

    [Fact]
    public async Task Connection_AppliesBoundedCommandTimeout()
    {
        using var database = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(database.DatabasePath);
        await new SqliteSchema(factory).InitializeAsync();

        await using var connection = await factory.OpenAsync();
        using var command = connection.CreateCommand();

        Assert.Equal(1, command.CommandTimeout);
    }

    [Fact]
    public async Task Connection_CommandTimeoutFollowsConfiguredBusyTimeout()
    {
        using var database = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(database.DatabasePath, busyTimeoutMilliseconds: 1234);
        await new SqliteSchema(factory).InitializeAsync();

        await using var connection = await factory.OpenAsync();
        using var command = connection.CreateCommand();

        Assert.Equal(2, command.CommandTimeout);
    }

    [Fact]
    public void ConnectionFactory_RejectsNegativeBusyTimeout()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SqliteConnectionFactory("notes.db", busyTimeoutMilliseconds: -1));
    }

    private static async Task<int> ScalarIntAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
