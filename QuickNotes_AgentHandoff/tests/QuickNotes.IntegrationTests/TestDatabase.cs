using Microsoft.Data.Sqlite;
using QuickNotes.Application;
using QuickNotes.Domain;
using QuickNotes.Infrastructure.Sqlite;

namespace QuickNotes.IntegrationTests;

internal sealed class TestDatabase : IDisposable
{
    private const string VersionOneDdl = """
        CREATE TABLE categories (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL COLLATE NOCASE UNIQUE,
            default_color TEXT NOT NULL,
            is_system INTEGER NOT NULL CHECK (is_system IN (0, 1))
        );
        CREATE TABLE tags (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL COLLATE NOCASE UNIQUE
        );
        CREATE TABLE notes (
            id TEXT PRIMARY KEY,
            title TEXT NULL,
            body TEXT NOT NULL,
            created_at_utc TEXT NOT NULL,
            modified_at_utc TEXT NOT NULL,
            version INTEGER NOT NULL CHECK (version >= 1),
            lifecycle INTEGER NOT NULL CHECK (lifecycle IN (0, 1, 2)),
            category_id TEXT NOT NULL,
            is_pinned INTEGER NOT NULL CHECK (is_pinned IN (0, 1)),
            display_intent INTEGER NOT NULL CHECK (display_intent IN (0, 1)),
            FOREIGN KEY (category_id) REFERENCES categories(id) ON DELETE RESTRICT,
            CHECK (lifecycle = 0 OR display_intent = 0)
        );
        CREATE TABLE note_tags (
            note_id TEXT NOT NULL,
            tag_id TEXT NOT NULL,
            PRIMARY KEY (note_id, tag_id),
            FOREIGN KEY (note_id) REFERENCES notes(id) ON DELETE CASCADE,
            FOREIGN KEY (tag_id) REFERENCES tags(id) ON DELETE CASCADE
        );
        CREATE INDEX ix_notes_category_id ON notes(category_id);
        CREATE INDEX ix_notes_modified_at_utc ON notes(modified_at_utc DESC);
        CREATE INDEX ix_note_tags_tag_id ON note_tags(tag_id);
        """;

    private readonly TemporaryDatabase _temporary = new();

    public TestDatabase()
    {
        Reopen();
    }

    public SqliteConnectionFactory Factory { get; private set; } = null!;
    public ICatalogRepository Catalog { get; private set; } = null!;
    public INoteRepository Notes { get; private set; } = null!;
    public string DatabasePath => _temporary.DatabasePath;

    public static async Task<TestDatabase> CreateAsync(int busyTimeoutMilliseconds = 250)
    {
        var database = new TestDatabase();
        await new SqliteSchema(database.Factory).InitializeAsync();
        return database;
    }

    public static async Task CreateVersionOneAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("Database path must not be empty.", nameof(databasePath));
        }

        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys=ON;";
            await pragma.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = VersionOneDdl;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO categories (id, name, default_color, is_system)
                VALUES ($id, $name, $default_color, 1);
                """;
            command.Parameters.AddWithValue("$id", Category.UncategorizedId.ToString("D"));
            command.Parameters.AddWithValue("$name", "未分类");
            command.Parameters.AddWithValue("$default_color", "#E6E6FA");
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version=1;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public void Reopen(int busyTimeoutMilliseconds = 250)
    {
        Factory = new SqliteConnectionFactory(_temporary.DatabasePath, busyTimeoutMilliseconds);
        Catalog = new SqliteCatalogRepository(Factory);
        Notes = new SqliteNoteRepository(Factory);
    }

    public void Dispose() => _temporary.Dispose();
}

// Alias used by Task 4/6 migration and restore tests.
internal static class V1DatabaseFixture
{
    public static Task CreateAsync(
        string databasePath,
        CancellationToken cancellationToken = default) =>
        TestDatabase.CreateVersionOneAsync(databasePath, cancellationToken);
}
