using Microsoft.Data.Sqlite;
using QuickNotes.Domain;
using QuickNotes.Infrastructure.Sqlite;
using Xunit;

namespace QuickNotes.IntegrationTests;

public sealed class SqliteSchemaTests
{
    [Fact]
    public async Task Initialize_CreatesVersionTwoWithSettingsAndMeta()
    {
        using var database = new TestDatabase();
        await new SqliteSchema(database.Factory).InitializeAsync();

        Assert.Equal(2, await ReadUserVersionAsync(database.Factory));
        Assert.Equal(1, await ScalarLongAsync(database.Factory, "SELECT COUNT(*) FROM database_meta;"));
        Assert.Equal(1, await ScalarLongAsync(database.Factory, "SELECT COUNT(*) FROM app_settings;"));
        Assert.Equal(16, await ScalarLongAsync(database.Factory, "SELECT default_font_size FROM app_settings WHERE id=1;"));
    }

    [Fact]
    public async Task Initialize_SeedsMetaSettingsAndSystemCategory()
    {
        using var database = new TestDatabase();
        await new SqliteSchema(database.Factory).InitializeAsync();

        Assert.Equal(1, await ScalarLongAsync(database.Factory, "SELECT COUNT(*) FROM categories WHERE is_system = 1;"));
        Assert.Equal(0, await ScalarLongAsync(database.Factory, "SELECT change_sequence FROM database_meta WHERE id = 1;"));
        var databaseId = await ScalarStringAsync(
            database.Factory,
            "SELECT database_id FROM database_meta WHERE id = 1;");
        Assert.True(Guid.TryParse(databaseId, out _));
        Assert.Equal(1, await ScalarLongAsync(database.Factory, "SELECT version FROM app_settings WHERE id = 1;"));
        Assert.Equal(0, await ScalarLongAsync(database.Factory, "SELECT global_hidden FROM app_settings WHERE id = 1;"));
        Assert.Equal(1, await ScalarLongAsync(database.Factory, "SELECT notifications_enabled FROM app_settings WHERE id = 1;"));
        Assert.Equal(
            "[\"Pin\",\"More\"]",
            await ScalarStringAsync(database.Factory, "SELECT top_bar_config_json FROM app_settings WHERE id = 1;"));
        Assert.Equal("ok", await ScalarStringAsync(database.Factory, "PRAGMA integrity_check;"));
    }

    [Fact]
    public async Task Reopen_PreservesSchemaSeedsAndReportsNoForeignKeyViolations()
    {
        using var database = new TemporaryDatabase();
        await new SqliteSchema(new SqliteConnectionFactory(database.DatabasePath)).InitializeAsync();
        var reopenedFactory = new SqliteConnectionFactory(database.DatabasePath);

        await new SqliteSchema(reopenedFactory).InitializeAsync();

        await using var connection = await reopenedFactory.OpenAsync();
        Assert.Equal(2, await ScalarIntAsync(connection, "PRAGMA user_version;"));
        Assert.Equal(1, await ScalarIntAsync(connection, "PRAGMA foreign_keys;"));
        Assert.Equal(1, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM database_meta;"));
        Assert.Equal(1, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM app_settings;"));
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task Initialize_WhenVersionOne_ThrowsSchemaMigrationRequired()
    {
        using var database = new TestDatabase();
        await TestDatabase.CreateVersionOneAsync(database.DatabasePath);

        var exception = await Assert.ThrowsAsync<SchemaMigrationRequiredException>(
            () => new SqliteSchema(database.Factory).InitializeAsync());

        Assert.Equal(1, exception.CurrentVersion);
        Assert.Equal(1, await ReadUserVersionAsync(database.Factory));
        Assert.Equal(
            0,
            await ScalarLongAsync(
                database.Factory,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('database_meta', 'app_settings');"));
    }

    [Fact]
    public async Task Initialize_WhenSchemaVersionIsNewer_ThrowsNotSupported()
    {
        using var database = new TestDatabase();
        await ExecuteAsync(database.Factory, "PRAGMA user_version=3;");

        await Assert.ThrowsAsync<NotSupportedException>(
            () => new SqliteSchema(database.Factory).InitializeAsync());

        Assert.Equal(3, await ReadUserVersionAsync(database.Factory));
    }

    [Fact]
    public async Task ChangeMarker_ReadReturnsStableIdentityAndAdvancedSequence()
    {
        using var database = await TestDatabase.CreateAsync();
        await using var connection = await database.Factory.OpenAsync();
        await using var transaction = connection.BeginTransaction();

        var initial = await SqliteChangeMarker.ReadAsync(connection, transaction);
        await SqliteChangeMarker.BumpAsync(connection, transaction);
        var bumped = await SqliteChangeMarker.ReadAsync(connection, transaction);

        Assert.Equal(initial.DatabaseId, bumped.DatabaseId);
        Assert.Equal(initial.ChangeSequence + 1, bumped.ChangeSequence);
        await transaction.CommitAsync();
    }

    private static async Task<int> ReadUserVersionAsync(SqliteConnectionFactory factory)
    {
        await using var connection = await factory.OpenAsync();
        return await ScalarIntAsync(connection, "PRAGMA user_version;");
    }

    private static async Task<long> ScalarLongAsync(SqliteConnectionFactory factory, string sql)
    {
        await using var connection = await factory.OpenAsync();
        return Convert.ToInt64(await ScalarAsync(connection, sql));
    }

    private static async Task<string> ScalarStringAsync(SqliteConnectionFactory factory, string sql)
    {
        await using var connection = await factory.OpenAsync();
        return Convert.ToString(await ScalarAsync(connection, sql))!;
    }

    private static async Task ExecuteAsync(SqliteConnectionFactory factory, string sql)
    {
        await using var connection = await factory.OpenAsync();
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

    private static async Task<int> ScalarIntAsync(SqliteConnection connection, string sql)
    {
        return Convert.ToInt32(await ScalarAsync(connection, sql));
    }
}
