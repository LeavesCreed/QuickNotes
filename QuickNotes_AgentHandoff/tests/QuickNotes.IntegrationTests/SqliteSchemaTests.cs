using Microsoft.Data.Sqlite;
using QuickNotes.Domain;
using QuickNotes.Infrastructure.Sqlite;
using Xunit;

namespace QuickNotes.IntegrationTests;

public sealed class SqliteSchemaTests
{
    [Fact]
    public async Task Initialize_CreatesVersionOneSchemaAndUncategorizedCategory()
    {
        using var database = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(database.DatabasePath);

        await new SqliteSchema(factory).InitializeAsync();

        await using var connection = await factory.OpenAsync();
        Assert.Equal(1, await ScalarIntAsync(connection, "PRAGMA user_version;"));
        Assert.Equal(1, await ScalarIntAsync(connection, "PRAGMA foreign_keys;"));
        Assert.Equal("ok", await ScalarStringAsync(connection, "PRAGMA integrity_check;"));
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM categories WHERE id = $id AND is_system = 1;";
        command.Parameters.AddWithValue("$id", Category.UncategorizedId.ToString("D"));
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Reopen_PreservesSchemaAndReportsNoForeignKeyViolations()
    {
        using var database = new TemporaryDatabase();
        await new SqliteSchema(new SqliteConnectionFactory(database.DatabasePath)).InitializeAsync();
        var reopenedFactory = new SqliteConnectionFactory(database.DatabasePath);

        await new SqliteSchema(reopenedFactory).InitializeAsync();

        await using var connection = await reopenedFactory.OpenAsync();
        Assert.Equal(1, await ScalarIntAsync(connection, "PRAGMA user_version;"));
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.False(await reader.ReadAsync());
    }

    private static async Task<int> ScalarIntAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync())!;
    }
}
