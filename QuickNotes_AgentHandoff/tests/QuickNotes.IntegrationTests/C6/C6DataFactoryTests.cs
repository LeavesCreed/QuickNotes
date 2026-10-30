using System.Globalization;
using QuickNotes.C6.Harness;
using QuickNotes.Infrastructure.Sqlite;

namespace QuickNotes.IntegrationTests.C6;

public sealed class C6DataFactoryTests
{
    [Fact]
    public async Task DataFactory_SeedsRequestedHistoryAndVisibleNotes()
    {
        using var temporary = new C6TemporaryDirectory();

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
        using var temporary = new C6TemporaryDirectory();

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
        using var first = new C6TemporaryDirectory();
        using var second = new C6TemporaryDirectory();

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
        Assert.Contains(C6DataFactory.DefaultTestRunId, firstBody, StringComparison.Ordinal);
        Assert.DoesNotContain(first.RootPath, firstBody, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\", firstBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DataFactory_BodiesCarryTheSuppliedRunId()
    {
        using var first = new C6TemporaryDirectory();
        using var second = new C6TemporaryDirectory();
        const string firstRunId = "c6-fix-run-0001";
        const string secondRunId = "c6-fix-run-0002";

        await C6DataFactory.SeedAsync(first.RootPath, notes: 12, visible: 3, firstRunId);
        await C6DataFactory.SeedAsync(second.RootPath, notes: 12, visible: 3, secondRunId);

        var firstBodies = await ReadBodiesAsync(first.RootPath);
        var secondBodies = await ReadBodiesAsync(second.RootPath);

        Assert.Equal(12, firstBodies.Count);
        Assert.Equal(12, secondBodies.Count);
        Assert.All(firstBodies, body =>
        {
            Assert.Contains(C6DataFactory.SyntheticBodyMarker, body, StringComparison.Ordinal);
            Assert.Contains(firstRunId, body, StringComparison.Ordinal);
            Assert.DoesNotContain(C6DataFactory.DefaultTestRunId, body, StringComparison.Ordinal);
        });
        Assert.All(secondBodies, body =>
        {
            Assert.Contains(secondRunId, body, StringComparison.Ordinal);
            Assert.DoesNotContain(firstRunId, body, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task DataFactory_VisibleNotesHaveDistinctWindowLayouts()
    {
        using var temporary = new C6TemporaryDirectory();

        await C6DataFactory.SeedAsync(temporary.RootPath, notes: 150, visible: 20);
        var factory = new SqliteConnectionFactory(DatabasePath(temporary.RootPath));

        var distinctLayouts = await ScalarLongAsync(
            factory,
            "SELECT COUNT(*) FROM (SELECT DISTINCT window_left, window_top, window_width, window_height FROM notes WHERE display_intent = 1);");

        Assert.Equal(20, distinctLayouts);
    }

    [Fact]
    public async Task DataFactory_RejectsVisibleCountAboveHistory()
    {
        using var temporary = new C6TemporaryDirectory();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => C6DataFactory.SeedAsync(temporary.RootPath, notes: 10, visible: 11));
    }

    [Fact]
    public async Task DataFactory_RefusesToSeedIntoAnExistingDatabase()
    {
        using var temporary = new C6TemporaryDirectory();
        await C6DataFactory.SeedAsync(temporary.RootPath, notes: 5, visible: 1);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => C6DataFactory.SeedAsync(temporary.RootPath, notes: 5, visible: 1));
    }

    [Fact]
    public async Task DataFactory_RefusesDataRootOutsideTheC6TestArea()
    {
        using var temporary = new TemporaryDirectory();

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => C6DataFactory.SeedAsync(temporary.RootPath, notes: 5, visible: 1));

        Assert.Contains(C6DataFactory.RunRootSegment, exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(DatabasePath(temporary.RootPath)));
    }

    [Fact]
    public async Task DataFactory_AcceptsRunIdDerivedRoot()
    {
        var runId = Guid.NewGuid().ToString("N");
        var rootPath = Path.Combine(Path.GetTempPath(), runId);
        try
        {
            var result = await C6DataFactory.SeedAsync(rootPath, notes: 5, visible: 1, runId);

            Assert.Equal(5, result.NoteIds.Count);
            Assert.All(
                await ReadBodiesAsync(rootPath),
                body => Assert.Contains(runId, body, StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task DataFactory_RejectsEmptyRunId(string runId)
    {
        using var temporary = new C6TemporaryDirectory();

        await Assert.ThrowsAsync<ArgumentException>(
            () => C6DataFactory.SeedAsync(temporary.RootPath, notes: 5, visible: 1, runId));
    }

    private static string DatabasePath(string dataRoot) =>
        Path.Combine(dataRoot, "notes.db");

    private static async Task<IReadOnlyList<string>> ReadBodiesAsync(string dataRoot)
    {
        var factory = new SqliteConnectionFactory(DatabasePath(dataRoot));
        await using var connection = await factory.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT body FROM notes ORDER BY id;";
        await using var reader = await command.ExecuteReaderAsync();
        var bodies = new List<string>();
        while (await reader.ReadAsync())
        {
            bodies.Add(reader.GetString(0));
        }

        return bodies;
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
