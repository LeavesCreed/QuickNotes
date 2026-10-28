using Microsoft.Data.Sqlite;
using QuickNotes.Domain;

namespace QuickNotes.Infrastructure.Sqlite;

public sealed class SqliteSchema
{
    public const int CurrentVersion = 2;

    private const string CategoriesTableDdl = """
        CREATE TABLE categories (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL COLLATE NOCASE UNIQUE,
            default_color TEXT NOT NULL,
            is_system INTEGER NOT NULL CHECK (is_system IN (0, 1))
        );
        """;

    private const string TagsTableDdl = """
        CREATE TABLE tags (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL COLLATE NOCASE UNIQUE
        );
        """;

    // Shared with the Task 4 migrator so new databases and migrated databases
    // cannot drift on the v2 notes columns.
    internal const string ColorModeColumnDdl =
        "color_mode INTEGER NOT NULL DEFAULT 0 CHECK (color_mode IN (0, 1))";

    internal const string ColorHexColumnDdl =
        "color_hex TEXT NULL CHECK (color_hex IS NULL OR length(color_hex) = 7)";

    internal const string FontSizeColumnDdl =
        "font_size INTEGER NULL CHECK (font_size IS NULL OR (font_size BETWEEN 8 AND 72))";

    internal const string WindowLeftColumnDdl =
        "window_left REAL NULL";

    internal const string WindowTopColumnDdl =
        "window_top REAL NULL";

    internal const string WindowWidthColumnDdl =
        "window_width REAL NULL CHECK (window_width IS NULL OR window_width > 0)";

    internal const string WindowHeightColumnDdl =
        "window_height REAL NULL CHECK (window_height IS NULL OR window_height > 0)";

    internal const string DatabaseMetaTableDdl = """
        CREATE TABLE database_meta (
            id INTEGER PRIMARY KEY CHECK (id = 1),
            database_id TEXT NOT NULL,
            change_sequence INTEGER NOT NULL CHECK (change_sequence >= 0)
        );
        """;

    internal const string ApplicationSettingsTableDdl = """
        CREATE TABLE app_settings (
            id INTEGER PRIMARY KEY CHECK (id = 1),
            version INTEGER NOT NULL CHECK (version >= 1),
            default_font_size INTEGER NOT NULL CHECK (default_font_size BETWEEN 8 AND 72),
            global_hidden INTEGER NOT NULL CHECK (global_hidden IN (0, 1)),
            notifications_enabled INTEGER NOT NULL CHECK (notifications_enabled IN (0, 1)),
            top_bar_config_json TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL
        );
        """;

    private static readonly string NotesTableDdl = $"""
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
            {ColorModeColumnDdl},
            {ColorHexColumnDdl},
            {FontSizeColumnDdl},
            {WindowLeftColumnDdl},
            {WindowTopColumnDdl},
            {WindowWidthColumnDdl},
            {WindowHeightColumnDdl},
            FOREIGN KEY (category_id) REFERENCES categories(id) ON DELETE RESTRICT,
            CHECK (lifecycle = 0 OR display_intent = 0)
        );
        """;

    private const string NoteTagsTableDdl = """
        CREATE TABLE note_tags (
            note_id TEXT NOT NULL,
            tag_id TEXT NOT NULL,
            PRIMARY KEY (note_id, tag_id),
            FOREIGN KEY (note_id) REFERENCES notes(id) ON DELETE CASCADE,
            FOREIGN KEY (tag_id) REFERENCES tags(id) ON DELETE CASCADE
        );
        """;

    private const string IndexesDdl = """
        CREATE INDEX ix_notes_category_id ON notes(category_id);
        CREATE INDEX ix_notes_modified_at_utc ON notes(modified_at_utc DESC);
        CREATE INDEX ix_note_tags_tag_id ON note_tags(tag_id);
        """;

    private static readonly string VersionTwoDdl = string.Join(
        Environment.NewLine,
        CategoriesTableDdl,
        TagsTableDdl,
        NotesTableDdl,
        NoteTagsTableDdl,
        DatabaseMetaTableDdl,
        ApplicationSettingsTableDdl,
        IndexesDdl);

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
        if (version == CurrentVersion)
        {
            return;
        }

        if (version == 1)
        {
            throw new SchemaMigrationRequiredException(version, CurrentVersion);
        }

        if (version > CurrentVersion)
        {
            throw new NotSupportedException(
                $"Database schema version {version} is newer than the supported version {CurrentVersion}.");
        }

        if (version != 0)
        {
            throw new NotSupportedException($"Database schema version {version} is not supported.");
        }

        await ExecuteAsync(connection, transaction, VersionTwoDdl, cancellationToken);
        await SeedSystemCategoryAsync(connection, transaction, cancellationToken);
        await SeedDatabaseMetaAsync(connection, transaction, cancellationToken);
        await SeedApplicationSettingsAsync(connection, transaction, cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"PRAGMA user_version={CurrentVersion};";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task SeedSystemCategoryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
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

    private static async Task SeedDatabaseMetaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO database_meta (id, database_id, change_sequence)
            VALUES (1, $database_id, 0);
            """;
        command.Parameters.AddWithValue("$database_id", Guid.NewGuid().ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SeedApplicationSettingsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var settings = ApplicationSettings.CreateDefault(DateTimeOffset.UtcNow);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO app_settings (
                id,
                version,
                default_font_size,
                global_hidden,
                notifications_enabled,
                top_bar_config_json,
                updated_at_utc)
            VALUES (1, $version, $default_font_size, $global_hidden, $notifications_enabled, $top_bar_config_json, $updated_at_utc);
            """;
        command.Parameters.AddWithValue("$version", settings.Version);
        command.Parameters.AddWithValue("$default_font_size", settings.DefaultFontSize);
        command.Parameters.AddWithValue("$global_hidden", settings.GlobalHidden ? 1 : 0);
        command.Parameters.AddWithValue("$notifications_enabled", settings.NotificationsEnabled ? 1 : 0);
        command.Parameters.AddWithValue(
            "$top_bar_config_json",
            SqliteTypeMapping.SerializeTopBarConfig(settings.TopBar));
        command.Parameters.AddWithValue("$updated_at_utc", SqliteTypeMapping.UtcString(settings.UpdatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
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
