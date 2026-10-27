using Microsoft.Data.Sqlite;
using StickyNotes.Domain;

namespace StickyNotes.Infrastructure.Sqlite;

public sealed class SqliteSchema
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

    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteSchema(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var version = await ReadUserVersionAsync(connection, transaction, cancellationToken);
        if (version == 1)
        {
            return;
        }

        if (version > 1)
        {
            throw new NotSupportedException($"Database schema version {version} is newer than the supported version 1.");
        }

        if (version != 0)
        {
            throw new NotSupportedException($"Database schema version {version} is not supported.");
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = VersionOneDdl;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
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
            command.Transaction = transaction;
            command.CommandText = "PRAGMA user_version=1;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<int> ReadUserVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }
}
