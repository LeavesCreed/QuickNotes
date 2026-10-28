using System.Globalization;
using System.Text.RegularExpressions;
using QuickNotes.Application;
using QuickNotes.Domain;
using QuickNotes.Infrastructure.Sqlite;
using Xunit;

namespace QuickNotes.IntegrationTests;

public sealed class SqliteChangeDetectorTests
{
    private static readonly Regex FingerprintPattern = new(
        "^schema=2;db=[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12};seq=[0-9]+$",
        RegexOptions.CultureInvariant);

    [Fact]
    public async Task GetFingerprint_IsStableWithoutChanges_AndChangesWithWrites()
    {
        using var database = await TestDatabase.CreateAsync();
        var detector = new SqliteChangeDetector(database.DatabasePath);

        var initial = await detector.GetFingerprintAsync();
        var initialAgain = await detector.GetFingerprintAsync();
        Assert.Equal(initial, initialAgain);
        Assert.Matches(FingerprintPattern, initial);

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
    public async Task GetFingerprint_ReturnsDeterministicMetaIdentityAndSequence()
    {
        using var database = await TestDatabase.CreateAsync();
        var detector = new SqliteChangeDetector(database.DatabasePath);
        var (databaseId, changeSequence) = await ReadMetaAsync(database.Factory);

        var fingerprint = await detector.GetFingerprintAsync();

        Assert.Equal(
            string.Create(
                CultureInfo.InvariantCulture,
                $"schema=2;db={databaseId:D};seq={changeSequence}"),
            fingerprint);
        Assert.Equal(0L, changeSequence);
    }

    [Fact]
    public async Task SameLengthCategoryRename_ChangesFingerprint()
    {
        using var database = new TestDatabase();
        await new SqliteSchema(database.Factory).InitializeAsync();
        var catalog = new SqliteCatalogRepository(database.Factory);
        var detector = new SqliteChangeDetector(database.DatabasePath);
        var category = new Category
        {
            Id = Guid.NewGuid(),
            Name = "工作",
            DefaultColor = "#E6E6FA",
            IsSystem = false
        };
        await catalog.CreateCategoryAsync(category);
        var before = await detector.GetFingerprintAsync();

        await catalog.UpdateCategoryAsync(category.Id, "生活", category.DefaultColor);

        var after = await detector.GetFingerprintAsync();
        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task RolledBackWrite_DoesNotAdvanceChangeSequence()
    {
        using var database = await TestDatabase.CreateAsync();
        var detector = new SqliteChangeDetector(database.DatabasePath);
        var before = await detector.GetFingerprintAsync();
        var beforeSequence = await ReadSequenceAsync(database.Factory);
        var note = TestData.NewNote();

        await using (var connection = await database.Factory.OpenAsync())
        await using (var transaction = connection.BeginTransaction())
        {
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO notes (
                        id,
                        title,
                        body,
                        created_at_utc,
                        modified_at_utc,
                        version,
                        lifecycle,
                        category_id,
                        is_pinned,
                        display_intent)
                    VALUES ($id, $title, $body, $created_at_utc, $modified_at_utc, 1, 0, $category_id, 0, 0);
                    """;
                command.Parameters.AddWithValue("$id", note.Id.ToString("D"));
                command.Parameters.AddWithValue("$title", (object?)note.Title ?? DBNull.Value);
                command.Parameters.AddWithValue("$body", note.Body);
                command.Parameters.AddWithValue(
                    "$created_at_utc",
                    note.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue(
                    "$modified_at_utc",
                    note.ModifiedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$category_id", note.CategoryId.ToString("D"));
                await command.ExecuteNonQueryAsync();
            }

            await SqliteChangeMarker.BumpAsync(connection, transaction);
            await transaction.RollbackAsync();
        }

        Assert.Equal(0L, await ScalarLongAsync(database.Factory, "SELECT COUNT(*) FROM notes;"));
        Assert.Equal(beforeSequence, await ReadSequenceAsync(database.Factory));
        Assert.Equal(before, await detector.GetFingerprintAsync());
    }

    [Fact]
    public async Task TagRelationAndColorChanges_AdvanceSequence()
    {
        using var database = await TestDatabase.CreateAsync();
        var firstTag = new Tag { Id = Guid.NewGuid(), Name = "first" };
        var secondTag = new Tag { Id = Guid.NewGuid(), Name = "second" };
        await database.Catalog.CreateTagAsync(firstTag);
        await database.Catalog.CreateTagAsync(secondTag);
        var note = TestData.NewNote();
        await database.Notes.CreateAsync(note, new[] { firstTag.Id });
        var detector = new SqliteChangeDetector(database.DatabasePath);
        var before = await detector.GetFingerprintAsync();
        var beforeSequence = await ReadSequenceAsync(database.Factory);

        await database.Notes.UpdateAsync(
            note with
            {
                ColorMode = NoteColorMode.Manual,
                ColorHex = "#112233",
                ModifiedAtUtc = note.ModifiedAtUtc.AddMinutes(1)
            },
            1,
            new[] { secondTag.Id });

        var after = await detector.GetFingerprintAsync();
        Assert.NotEqual(before, after);
        Assert.Equal(beforeSequence + 1, await ReadSequenceAsync(database.Factory));
        Assert.Equal(new[] { secondTag.Id }, await database.Notes.GetTagIdsAsync(note.Id));
        Assert.Equal("#112233", (await database.Notes.GetAsync(note.Id))!.ColorHex);
    }

    [Fact]
    public async Task GetFingerprint_WhenMetaRowMissing_ThrowsChangeMarkerUnavailable()
    {
        using var database = await TestDatabase.CreateAsync();
        await ExecuteAsync(database.Factory, "DELETE FROM database_meta;");
        var detector = new SqliteChangeDetector(database.DatabasePath);

        var exception = await Assert.ThrowsAsync<BackupException>(
            () => detector.GetFingerprintAsync());

        Assert.Equal("change_detection", exception.Operation);
        Assert.Equal("change_marker_unavailable", exception.ErrorCode);
        Assert.False(exception.IsRetryable);
    }

    [Fact]
    public async Task GetFingerprint_WhenMetaTableMissing_ThrowsChangeMarkerUnavailable()
    {
        using var database = await TestDatabase.CreateAsync();
        await ExecuteAsync(database.Factory, "DROP TABLE database_meta;");
        var detector = new SqliteChangeDetector(database.DatabasePath);

        var exception = await Assert.ThrowsAsync<BackupException>(
            () => detector.GetFingerprintAsync());

        Assert.Equal("change_detection", exception.Operation);
        Assert.Equal("change_marker_unavailable", exception.ErrorCode);
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

    private static async Task<(Guid DatabaseId, long ChangeSequence)> ReadMetaAsync(
        SqliteConnectionFactory factory)
    {
        await using var connection = await factory.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT database_id, change_sequence FROM database_meta WHERE id = 1;";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (Guid.Parse(reader.GetString(0)), reader.GetInt64(1));
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

    private static async Task<long> ScalarLongAsync(
        SqliteConnectionFactory factory,
        string sql)
    {
        await using var connection = await factory.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(SqliteConnectionFactory factory, string sql)
    {
        await using var connection = await factory.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
