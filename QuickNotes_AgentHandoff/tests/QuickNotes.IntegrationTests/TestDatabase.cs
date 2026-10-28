using QuickNotes.Application;
using QuickNotes.Infrastructure.Sqlite;

namespace QuickNotes.IntegrationTests;

internal sealed class TestDatabase : IDisposable
{
    private readonly TemporaryDatabase _temporary = new();

    private TestDatabase() { }

    public SqliteConnectionFactory Factory { get; private set; } = null!;
    public ICatalogRepository Catalog { get; private set; } = null!;
    public INoteRepository Notes { get; private set; } = null!;
    public string DatabasePath => _temporary.DatabasePath;

    public static async Task<TestDatabase> CreateAsync(int busyTimeoutMilliseconds = 250)
    {
        var database = new TestDatabase();
        database.Reopen(busyTimeoutMilliseconds);
        await new SqliteSchema(database.Factory).InitializeAsync();
        return database;
    }

    public void Reopen(int busyTimeoutMilliseconds = 250)
    {
        Factory = new SqliteConnectionFactory(_temporary.DatabasePath, busyTimeoutMilliseconds);
        Catalog = new SqliteCatalogRepository(Factory);
        Notes = new SqliteNoteRepository(Factory);
    }

    public void Dispose() => _temporary.Dispose();
}
