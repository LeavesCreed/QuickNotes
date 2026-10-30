using System.Globalization;
using QuickNotes.C6.Harness;
using QuickNotes.Infrastructure.Sqlite;

namespace QuickNotes.IntegrationTests.C6;

public sealed class C6DataFactoryTests
{
    [Fact]
    public async Task DataFactory_SeedsRequestedHistoryAndVisibleNotes()
    {
        using var temporary = new TemporaryDirectory();

        var result = await C6DataFactory.SeedAsync(temporary.RootPath, notes: 1000, visible: 20);

        Assert.Equal(1000, result.NoteIds.Count);
        Assert.Equal(20, result.VisibleNoteIds.Count);
        Assert.Equal(1000, result.NoteIds.Distinct().Count());
        Assert.Equal(20, result.VisibleNoteIds.Distinct().Count());
        Assert.All(result.VisibleNoteIds, id => Assert.Contains(id, result.NoteIds));
    }

    [Fact]
    public async Task DataFactory_UsesRealSchemaAndFixedDistribution()
    {
        using var temporary = new TemporaryDirectory();

        var result = await C6DataFactory.SeedAsync(temporary.RootPath, notes: 120, visible: 6);
        var factory = new SqliteConnectionFactory(DatabasePath(temporary.RootPath));

        Assert.Equal(2, await ScalarLongAsync(factory, "PRAGMA user_version;"));
        Assert.Equal("ok", await ScalarStringAsync(factory, "PRAGMA integrity_check;"));
        Assert.Equal(1, await ScalarLongAsync(factory, "SELECT COUNT(*) FROM categories WHERE is_system = 1;"));
        Assert.Equal(4, await ScalarLongAsync(factory, "SELECT COUNT(*) FROM categories;"));
        Assert.Equal(5, await ScalarLongAsync(factory, "SELECT COUNT(*) FROM tags;"));
        Assert.Equal(120, await ScalarLongAsync(factory, "SELECT COUNT(*) FROM notes;"));
        Assert.Equal(6, await ScalarLongAsync(factory, "SELECT COUNT(*) FROM notes WHERE display_intent = 1;"));
        Assert.Equal(
            0,
            await ScalarLongAsync(
                factory,
                "SELECT COUNT(*) FROM notes WHERE display_intent = 1 AND lifecycle <> 0;"));
        Assert.True(
            await ScalarLongAsync(factory, "SELECT COUNT(*) FROM notes WHERE lifecycle = 1;") > 0);
        Assert.True(
            await ScalarLongAsync(factory, "SELECT COUNT(*) FROM notes WHERE lifecycle = 2;") > 0);
        Assert.True(await ScalarLongAsync(factory, "SELECT COUNT(*) FROM note_tags;") > 0);
        Assert.Equal(
            6,
            await ScalarLongAsync(
                factory,
                "SELECT COUNT(*) FROM notes WHERE window_left IS NOT NULL AND window_top IS NOT NULL;"));
        Assert.Equal(6, result.VisibleNoteIds.Count);
    }

    [Fact]
    public async Task DataFactory_IsDeterministicForSameInputs()
    {
        using var first = new TemporaryDirectory();
        using var second = new TemporaryDirectory();

        var firstResult = await C6DataFactory.SeedAsync(first.RootPath, notes: 40, visible: 4);
        var secondResult = await C6DataFactory.SeedAsync(second.RootPath, notes: 40, visible: 4);

        Assert.Equal(firstResult.NoteIds, secondResult.NoteIds);
        Assert.Equal(firstResult.VisibleNoteIds, secondResult.VisibleNoteIds);

        var firstFactory = new SqliteConnectionFactory(DatabasePath(first.RootPath));
        var secondFactory = new SqliteConnectionFactory(DatabasePath(second.RootPath));
        var firstBody = await ScalarStringAsync(
            firstFactory,
            "SELECT body FROM notes ORDER BY id LIMIT 1;");
        var secondBody = await ScalarStringAsync(
            secondFactory,
            "SELECT body FROM notes ORDER BY id LIMIT 1;");
        Assert.Equal(firstBody, secondBody);
        Assert.DoesNotContain(first.RootPath, firstBody, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\", firstBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DataFactory_RejectsVisibleCountAboveHistory()
    {
        using var temporary = new TemporaryDirectory();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => C6DataFactory.SeedAsync(temporary.RootPath, notes: 10, visible: 11));
    }

    [Fact]
    public async Task DataFactory_RefusesToSeedIntoAnExistingDatabase()
    {
        using var temporary = new TemporaryDirectory();
        await C6DataFactory.SeedAsync(temporary.RootPath, notes: 5, visible: 1);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => C6DataFactory.SeedAsync(temporary.RootPath, notes: 5, visible: 1));
    }

    private static string DatabasePath(string dataRoot) =>
        Path.Combine(dataRoot, "notes.db");

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

    private static async Task<string> ScalarStringAsync(
        SqliteConnectionFactory factory,
        string sql)
    {
        await using var connection = await factory.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(
            await command.ExecuteScalarAsync(),
            CultureInfo.InvariantCulture) ?? string.Empty;
    }
}
