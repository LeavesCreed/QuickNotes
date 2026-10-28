using QuickNotes.Application;
using QuickNotes.Domain;
using QuickNotes.Infrastructure.Sqlite;
using Xunit;

namespace QuickNotes.IntegrationTests;

public sealed class SqliteChangeDetectorTests
{
    [Fact]
    public async Task GetFingerprint_IsStableWithoutChanges_AndChangesWithWrites()
    {
        using var database = await TestDatabase.CreateAsync();
        var detector = new SqliteChangeDetector(database.DatabasePath);

        var initial = await detector.GetFingerprintAsync();
        var initialAgain = await detector.GetFingerprintAsync();
        Assert.Equal(initial, initialAgain);
        Assert.StartsWith("schema=2;", initial, StringComparison.Ordinal);

        var tag = new Tag { Id = Guid.NewGuid(), Name = "fingerprint-tag" };
        await database.Catalog.CreateTagAsync(tag);
        var afterTag = await detector.GetFingerprintAsync();
        Assert.NotEqual(initial, afterTag);

        var note = TestData.NewNote();
        await database.Notes.CreateAsync(note, new[] { tag.Id });
        var afterNote = await detector.GetFingerprintAsync();
        Assert.NotEqual(afterTag, afterNote);

        await database.Notes.UpdateAsync(
            note with { Body = "updated", ModifiedAtUtc = note.ModifiedAtUtc.AddMinutes(1) },
            1,
            new[] { tag.Id });
        var afterUpdate = await detector.GetFingerprintAsync();
        Assert.NotEqual(afterNote, afterUpdate);

        await database.Notes.SetLifecycleAsync(
            note.Id,
            NoteLifecycle.Archived,
            2,
            note.ModifiedAtUtc.AddMinutes(2));
        var afterLifecycle = await detector.GetFingerprintAsync();
        Assert.NotEqual(afterUpdate, afterLifecycle);
    }

    [Fact]
    public async Task GetFingerprint_WhenSourceMissing_ThrowsSourceUnavailable()
    {
        using var temporary = new TemporaryDirectory();
        var detector = new SqliteChangeDetector(
            Path.Combine(temporary.RootPath, "missing.db"));

        var exception = await Assert.ThrowsAsync<BackupException>(
            () => detector.GetFingerprintAsync());

        Assert.Equal(BackupErrorCodes.SourceUnavailable, exception.ErrorCode);
        Assert.Equal("change_detection", exception.Operation);
    }

    [Fact]
    public void Constructor_EmptyPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => new SqliteChangeDetector(" "));
    }
}
