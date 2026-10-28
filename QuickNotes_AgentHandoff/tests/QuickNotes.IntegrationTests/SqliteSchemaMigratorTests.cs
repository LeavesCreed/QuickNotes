using Microsoft.Data.Sqlite;
using QuickNotes.Application;
using QuickNotes.Domain;
using QuickNotes.Infrastructure.Sqlite;
using Xunit;

namespace QuickNotes.IntegrationTests;

public sealed class SqliteSchemaMigratorTests
{
    [Fact]
    public async Task Migrate_V1ToV2_CreatesMetaAndDefaults()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.RootPath, "notes.db");
        await V1DatabaseFixture.CreateAsync(path);
        var migrator = new SqliteSchemaMigrator();

        var outcome = await migrator.MigrateToCurrentAsync(path, rotateIdentity: true);

        Assert.Equal(1, outcome.FromSchemaVersion);
        Assert.Equal(2, outcome.ToSchemaVersion);
        Assert.True(outcome.Migrated);
        Assert.False(outcome.IdentityRotated);
        Assert.NotEqual(Guid.Empty, outcome.DatabaseId);

        var factory = new SqliteConnectionFactory(path);
        Assert.Equal(2, await ReadUserVersionAsync(factory));
        Assert.Equal(1, await ScalarLongAsync(factory, "SELECT COUNT(*) FROM database_meta;"));
        Assert.Equal(
            outcome.DatabaseId.ToString("D"),
            await ScalarStringAsync(factory, "SELECT database_id FROM database_meta WHERE id = 1;"));
        Assert.Equal(1, await ScalarLongAsync(factory, "SELECT change_sequence FROM database_meta WHERE id = 1;"));
        Assert.Equal(1, await ScalarLongAsync(factory, "SELECT COUNT(*) FROM app_settings;"));
        Assert.Equal(1, await ScalarLongAsync(factory, "SELECT version FROM app_settings WHERE id = 1;"));
        Assert.Equal(16, await ScalarLongAsync(factory, "SELECT default_font_size FROM app_settings WHERE id = 1;"));
        Assert.Equal(
            "#E6E6FA",
            await ScalarStringAsync(
                factory,
                "SELECT default_color FROM categories WHERE id = '6f2a98f2-20de-4f4e-a2f4-8a570c4625d7';"));
        Assert.Equal("ok", await ScalarStringAsync(factory, "PRAGMA integrity_check;"));
        Assert.Equal(0, await ForeignKeyViolationCountAsync(factory));
    }

    [Fact]
    public async Task Migrate_V1ToV2_PreservesNotesAndAppliesAppearanceDefaults()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.RootPath, "notes.db");
        await V1DatabaseFixture.CreateAsync(path);
        var noteId = Guid.NewGuid();
        var factory = new SqliteConnectionFactory(path);
        await ExecuteAsync(
            factory,
            $"""
            INSERT INTO notes (
                id, title, body, created_at_utc, modified_at_utc, version,
                lifecycle, category_id, is_pinned, display_intent)
            VALUES (
                '{noteId:D}', 'existing title', 'existing body',
                '2026-10-28T09:30:00.0000000+00:00', '2026-10-28T09:30:00.0000000+00:00',
                3, 0, '{Category.UncategorizedId:D}', 1, 0);
            """);

        var outcome = await new SqliteSchemaMigrator().MigrateToCurrentAsync(path, rotateIdentity: true);

        Assert.True(outcome.Migrated);
        Assert.Equal(1, await ScalarLongAsync(factory, "SELECT COUNT(*) FROM notes;"));
        Assert.Equal(
            "existing body",
            await ScalarStringAsync(factory, $"SELECT body FROM notes WHERE id = '{noteId:D}';"));
        Assert.Equal(
            3,
            await ScalarLongAsync(factory, $"SELECT version FROM notes WHERE id = '{noteId:D}';"));
        Assert.Equal(
            0,
            await ScalarLongAsync(factory, $"SELECT color_mode FROM notes WHERE id = '{noteId:D}';"));
        Assert.Equal(
            1,
            await ScalarLongAsync(
                factory,
                $"SELECT COUNT(*) FROM notes WHERE id = '{noteId:D}' AND color_hex IS NULL AND font_size IS NULL AND window_left IS NULL AND window_top IS NULL AND window_width IS NULL AND window_height IS NULL;"));
    }

    [Fact]
    public async Task Migrate_AlreadyV2WithoutRotation_KeepsIdentity()
    {
        using var database = await TestDatabase.CreateAsync();
        var beforeId = await ScalarStringAsync(database.Factory, "SELECT database_id FROM database_meta WHERE id = 1;");
        var beforeSequence = await ScalarLongAsync(database.Factory, "SELECT change_sequence FROM database_meta WHERE id = 1;");

        var outcome = await new SqliteSchemaMigrator().MigrateToCurrentAsync(
            database.DatabasePath,
            rotateIdentity: false);

        Assert.False(outcome.Migrated);
        Assert.False(outcome.IdentityRotated);
        Assert.Equal(Guid.Parse(beforeId), outcome.DatabaseId);
        Assert.Equal(
            beforeId,
            await ScalarStringAsync(database.Factory, "SELECT database_id FROM database_meta WHERE id = 1;"));
        Assert.Equal(
            beforeSequence,
            await ScalarLongAsync(database.Factory, "SELECT change_sequence FROM database_meta WHERE id = 1;"));
        Assert.Equal(2, await ReadUserVersionAsync(database.Factory));
    }

    [Fact]
    public async Task Migrate_AlreadyV2WithRotation_RotatesIdentityAndAdvancesSequence()
    {
        using var database = await TestDatabase.CreateAsync();
        var beforeId = await ScalarStringAsync(database.Factory, "SELECT database_id FROM database_meta WHERE id = 1;");
        var beforeSequence = await ScalarLongAsync(database.Factory, "SELECT change_sequence FROM database_meta WHERE id = 1;");

        var outcome = await new SqliteSchemaMigrator().MigrateToCurrentAsync(
            database.DatabasePath,
            rotateIdentity: true);

        Assert.False(outcome.Migrated);
        Assert.True(outcome.IdentityRotated);
        var afterId = await ScalarStringAsync(database.Factory, "SELECT database_id FROM database_meta WHERE id = 1;");
        Assert.NotEqual(beforeId, afterId);
        Assert.Equal(Guid.Parse(afterId), outcome.DatabaseId);
        Assert.Equal(
            beforeSequence + 1,
            await ScalarLongAsync(database.Factory, "SELECT change_sequence FROM database_meta WHERE id = 1;"));
    }

    [Fact]
    public async Task Migrate_UnsupportedFutureSchema_IsRejectedAndUnchanged()
    {
        using var database = new TestDatabase();
        await TestDatabase.CreateVersionOneAsync(database.DatabasePath);
        await ExecuteAsync(database.Factory, "PRAGMA user_version=3;");

        var exception = await Assert.ThrowsAsync<SchemaMigrationException>(
            () => new SqliteSchemaMigrator().MigrateToCurrentAsync(
                database.DatabasePath,
                rotateIdentity: false));

        Assert.Equal("schema_unsupported", exception.ErrorCode);
        Assert.Equal(3, await ReadUserVersionAsync(database.Factory));
        Assert.Equal(
            0,
            await ScalarLongAsync(
                database.Factory,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('database_meta', 'app_settings');"));
    }

    [Fact]
    public async Task Migrate_UnsupportedEmptySchema_IsRejectedWithoutCreatingTables()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.RootPath, "empty.db");
        await using (var connection = await new SqliteConnectionFactory(path).OpenAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            await command.ExecuteScalarAsync();
        }

        var exception = await Assert.ThrowsAsync<SchemaMigrationException>(
            () => new SqliteSchemaMigrator().MigrateToCurrentAsync(path, rotateIdentity: false));

        Assert.Equal("schema_unsupported", exception.ErrorCode);
        Assert.Equal(
            0,
            await ScalarLongAsync(
                new SqliteConnectionFactory(path),
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';"));
    }

    [Fact]
    public async Task Migrate_MissingDatabase_IsRejectedWithoutCreatingIt()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.RootPath, "missing.db");

        var exception = await Assert.ThrowsAsync<SchemaMigrationException>(
            () => new SqliteSchemaMigrator().MigrateToCurrentAsync(path, rotateIdentity: false));

        Assert.Equal("database_missing", exception.ErrorCode);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Migrate_V1WithConflictingTable_FailsAndRollsBack()
    {
        using var database = new TestDatabase();
        await TestDatabase.CreateVersionOneAsync(database.DatabasePath);
        await ExecuteAsync(database.Factory, "CREATE TABLE database_meta (id INTEGER PRIMARY KEY, legacy TEXT);");

        var exception = await Assert.ThrowsAsync<SchemaMigrationException>(
            () => new SqliteSchemaMigrator().MigrateToCurrentAsync(
                database.DatabasePath,
                rotateIdentity: true));

        Assert.Equal("migration_failed", exception.ErrorCode);
        Assert.Equal(1, await ReadUserVersionAsync(database.Factory));
        Assert.Equal(
            0,
            await ScalarLongAsync(
                database.Factory,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'app_settings';"));
        Assert.Equal(
            "legacy",
            await ScalarStringAsync(
                database.Factory,
                "SELECT name FROM pragma_table_info('database_meta') WHERE name = 'legacy';"));
    }

    [Fact]
    public async Task Migrate_V1WithForeignKeyViolation_FailsVerificationAndRollsBack()
    {
        using var database = new TestDatabase();
        await TestDatabase.CreateVersionOneAsync(database.DatabasePath);
        var factory = database.Factory;
        await using (var connection = await factory.OpenAsync())
        {
            await ExecuteAsync(connection, "PRAGMA foreign_keys=OFF;");
            await ExecuteAsync(
                connection,
                $"""
                INSERT INTO notes (
                    id, title, body, created_at_utc, modified_at_utc, version,
                    lifecycle, category_id, is_pinned, display_intent)
                VALUES (
                    '{Guid.NewGuid():D}', 'orphan', 'orphan body',
                    '2026-10-28T09:30:00.0000000+00:00', '2026-10-28T09:30:00.0000000+00:00',
                    1, 0, '{Guid.NewGuid():D}', 0, 0);
                """);
        }

        var exception = await Assert.ThrowsAsync<SchemaMigrationException>(
            () => new SqliteSchemaMigrator().MigrateToCurrentAsync(
                database.DatabasePath,
                rotateIdentity: true));

        Assert.Equal("migration_verification_failed", exception.ErrorCode);
        Assert.Equal(1, await ReadUserVersionAsync(factory));
        Assert.Equal(
            0,
            await ScalarLongAsync(
                factory,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('database_meta', 'app_settings');"));
        Assert.Equal(1, await ScalarLongAsync(factory, "SELECT COUNT(*) FROM notes WHERE title = 'orphan';"));
    }

    private static async Task<int> ReadUserVersionAsync(SqliteConnectionFactory factory)
    {
        await using var connection = await factory.OpenAsync();
        return Convert.ToInt32(await ScalarAsync(connection, "PRAGMA user_version;"));
    }

    private static async Task<long> ScalarLongAsync(SqliteConnectionFactory factory, string sql)
    {
        await using var connection = await factory.OpenAsync();
        return Convert.ToInt64(await ScalarAsync(connection, sql));
    }

    private static async Task<string> ScalarStringAsync(SqliteConnectionFactory factory, string sql)
    {
        await using var connection = await factory.OpenAsync();
        return Convert.ToString(await ScalarAsync(connection, sql)) ?? string.Empty;
    }

    private static async Task<int> ForeignKeyViolationCountAsync(SqliteConnectionFactory factory)
    {
        await using var connection = await factory.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await command.ExecuteReaderAsync();
        var count = 0;
        while (await reader.ReadAsync())
        {
            count++;
        }

        return count;
    }

    private static async Task ExecuteAsync(SqliteConnectionFactory factory, string sql)
    {
        await using var connection = await factory.OpenAsync();
        await ExecuteAsync(connection, sql);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
