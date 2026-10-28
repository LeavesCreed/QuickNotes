using QuickNotes.Application;
using QuickNotes.Desktop.Recovery;

namespace QuickNotes.Desktop.Tests;

public sealed class RestoreJournalTests
{
    private static readonly DateTimeOffset Started =
        new(2026, 10, 28, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Read_MissingJournal_ReturnsNotFound()
    {
        using var temporary = new TestTemporaryDirectory();
        var store = CreateStore(temporary);

        var result = await store.ReadAsync();

        Assert.Equal(RestoreJournalReadStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task WriteThenRead_RoundTripsAllStages()
    {
        using var temporary = new TestTemporaryDirectory();
        var store = CreateStore(temporary);
        var journal = CreateJournal(RestoreStage.Switching);

        await store.WriteAsync(journal);
        var result = await store.ReadAsync();

        Assert.Equal(RestoreJournalReadStatus.Found, result.Status);
        Assert.Equal(journal, result.Journal);
    }

    [Fact]
    public async Task Read_InvalidJson_ReturnsInvalid()
    {
        using var temporary = new TestTemporaryDirectory();
        var store = CreateStore(temporary);
        Directory.CreateDirectory(Path.GetDirectoryName(store.JournalPath)!);
        await File.WriteAllTextAsync(store.JournalPath, "{");

        var result = await store.ReadAsync();

        Assert.Equal(RestoreJournalReadStatus.Invalid, result.Status);
        Assert.Equal("journal_json_invalid", result.Error);
    }

    [Fact]
    public async Task Read_UnknownFormatVersion_ReturnsInvalid()
    {
        using var temporary = new TestTemporaryDirectory();
        var store = CreateStore(temporary);
        Directory.CreateDirectory(Path.GetDirectoryName(store.JournalPath)!);
        await File.WriteAllTextAsync(
            store.JournalPath,
            """
            {
              "formatVersion": 99,
              "restoreId": "5a0f8c2e-6f4e-4f0e-9f6e-2c9a7b1d3e44",
              "stage": "Switching",
              "currentDatabasePath": "notes.db",
              "stagingDirectory": "staging",
              "startedAtUtc": "2026-10-28T09:30:00+00:00",
              "updatedAtUtc": "2026-10-28T09:31:00+00:00"
            }
            """);

        var result = await store.ReadAsync();

        Assert.Equal(RestoreJournalReadStatus.Invalid, result.Status);
        Assert.Equal("journal_invalid", result.Error);
    }

    [Fact]
    public async Task Write_ReplacesExistingJournalAtomically_NoTempLeftovers()
    {
        using var temporary = new TestTemporaryDirectory();
        var store = CreateStore(temporary);
        await store.WriteAsync(CreateJournal(RestoreStage.Staging));
        await store.WriteAsync(CreateJournal(RestoreStage.Completed));

        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(store.JournalPath)!, "*.tmp"));
        var result = await store.ReadAsync();
        Assert.Equal(RestoreStage.Completed, result.Journal!.Stage);
    }

    private static FileRestoreJournalStore CreateStore(TestTemporaryDirectory temporary) =>
        new(Path.Combine(temporary.RootPath, "recovery-journal.json"));

    private static RestoreJournal CreateJournal(RestoreStage stage) => new()
    {
        FormatVersion = RestoreJournal.CurrentFormatVersion,
        RestoreId = Guid.NewGuid(),
        Stage = stage,
        CurrentDatabasePath = Path.Combine("data", "notes.db"),
        StagingDirectory = Path.Combine("data", ".quicknotes-restore-test"),
        ProtectionSnapshotPath = Path.Combine("backups", "protection.zip"),
        RollbackPath = Path.Combine("data", ".quicknotes-restore-test", "rollback.db"),
        StartedAtUtc = Started,
        UpdatedAtUtc = Started.AddMinutes(1)
    };
}
