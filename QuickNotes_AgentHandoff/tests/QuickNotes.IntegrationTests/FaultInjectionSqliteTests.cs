using Microsoft.Data.Sqlite;
using QuickNotes.Application;
using QuickNotes.Domain;
using QuickNotes.Infrastructure.Drafts;
using QuickNotes.Infrastructure.Sqlite;
using Xunit;

namespace QuickNotes.IntegrationTests;

public sealed class FaultInjectionSqliteTests
{
    [Fact]
    public async Task SqliteFull_DuringSave_ReturnsStorageUnavailableAndKeepsDatabaseUsable()
    {
        using var database = await TestDatabase.CreateAsync();
        using var drafts = new TemporaryDirectory();
        var limitedFactory = new SqliteConnectionFactory(
            database.DatabasePath,
            configureConnection: LimitPageCount);
        var coordinator = new NoteSaveCoordinator(
            new SqliteNoteRepository(limitedFactory),
            new FileNoteDraftStore(drafts.DraftDirectory),
            new SaveRetryOptions
            {
                RetryDelays = new[] { TimeSpan.Zero, TimeSpan.Zero }
            });
        var note = TestData.NewNote() with
        {
            Body = new string('x', 512 * 1024)
        };

        var result = await coordinator.CreateAsync(note, Array.Empty<Guid>());

        Assert.Equal(SaveStatus.StorageUnavailable, result.Status);
        Assert.Equal(SaveErrorCode.StorageUnavailable, result.Error!.Code);
        Assert.False(result.Error.IsRetryable);
        Assert.Contains("sqlite_full", result.Error.Diagnostics, StringComparison.Ordinal);
        Assert.Equal(1, result.Attempts);

        var normalFactory = new SqliteConnectionFactory(
            database.DatabasePath,
            configureConnection: ResetPageCount);
        var normalNotes = new SqliteNoteRepository(normalFactory);
        Assert.Null(await normalNotes.GetAsync(note.Id));
        var after = await normalNotes.CreateAsync(
            TestData.NewNote(),
            Array.Empty<Guid>());
        Assert.Equal(1, after.Version);
    }

    [Fact]
    public async Task ReadOnlyDatabase_DuringSave_ReturnsStorageUnavailableWithoutChange()
    {
        using var database = await TestDatabase.CreateAsync();
        var existing = TestData.NewNote();
        await database.Notes.CreateAsync(existing, Array.Empty<Guid>());
        using var drafts = new TemporaryDirectory();
        var coordinator = new NoteSaveCoordinator(
            database.Notes,
            new FileNoteDraftStore(drafts.DraftDirectory),
            new SaveRetryOptions
            {
                RetryDelays = new[] { TimeSpan.Zero, TimeSpan.Zero }
            });
        var newNote = TestData.NewNote();
        File.SetAttributes(database.DatabasePath, FileAttributes.ReadOnly);
        try
        {
            var result = await coordinator.CreateAsync(newNote, Array.Empty<Guid>());

            Assert.Equal(SaveStatus.StorageUnavailable, result.Status);
            Assert.False(result.Error!.IsRetryable);
            Assert.Equal(
                "operation=create; error=sqlite_readonly",
                result.Error.Diagnostics);
        }
        finally
        {
            File.SetAttributes(database.DatabasePath, FileAttributes.Normal);
        }

        Assert.NotNull(await database.Notes.GetAsync(existing.Id));
        Assert.Null(await database.Notes.GetAsync(newNote.Id));
    }

    [Fact]
    public async Task ConcurrentUpdates_OneWinsOneConflictsWithoutLostUpdate()
    {
        using var database = await TestDatabase.CreateAsync();
        var note = TestData.NewNote() with { Body = "original" };
        await database.Notes.CreateAsync(note, Array.Empty<Guid>());
        using var drafts = new TemporaryDirectory();
        var coordinator = new NoteSaveCoordinator(
            database.Notes,
            new FileNoteDraftStore(drafts.DraftDirectory),
            new SaveRetryOptions
            {
                RetryDelays = new[] { TimeSpan.Zero, TimeSpan.Zero }
            });
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<SaveResult> UpdateAsync(string body) => Task.Run(async () =>
        {
            await start.Task;
            return await coordinator.UpdateAsync(
                note with
                {
                    Body = body,
                    ModifiedAtUtc = note.ModifiedAtUtc.AddMinutes(1)
                },
                1,
                Array.Empty<Guid>());
        });

        var first = UpdateAsync("first writer");
        var second = UpdateAsync("second writer");
        start.SetResult();
        var results = await Task.WhenAll(first, second);

        var saved = results.Single(result => result.Status == SaveStatus.Saved);
        Assert.Contains(results, result => result.Status == SaveStatus.Conflict);
        var loaded = await database.Notes.GetAsync(note.Id);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.Version);
        Assert.Equal(saved.SavedNote!.Body, loaded.Body);
    }

    private static void LimitPageCount(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA page_count;";
        var pages = Convert.ToInt64(command.ExecuteScalar());
        command.CommandText = $"PRAGMA max_page_count={pages};";
        command.ExecuteNonQuery();
    }

    private static void ResetPageCount(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA max_page_count=1000000;";
        command.ExecuteNonQuery();
    }
}
