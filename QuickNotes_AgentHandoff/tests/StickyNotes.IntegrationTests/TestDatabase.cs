using StickyNotes.Application;
using StickyNotes.Infrastructure.Sqlite;

namespace StickyNotes.IntegrationTests;

internal sealed class TestDatabase : IDisposable
{
    private readonly TemporaryDatabase _temporary = new();

    private TestDatabase() { }

    public SqliteConnectionFactory Factory { get; private set; } = null!;
    public ICatalogRepository Catalog { get; private set; } = null!;
    public INoteRepository Notes { get; private set; } = null!;

    public static async Task<TestDatabase> CreateAsync()
    {
        var database = new TestDatabase();
        database.Reopen();
        await new SqliteSchema(database.Factory).InitializeAsync();
        return database;
    }

    public void Reopen()
    {
        Factory = new SqliteConnectionFactory(_temporary.DatabasePath);
        Catalog = new SqliteCatalogRepository(Factory);
        Notes = new SqliteNoteRepository(Factory);
    }

    public void Dispose() => _temporary.Dispose();
}
