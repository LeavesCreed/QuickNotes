using Microsoft.Data.Sqlite;
using QuickNotes.Application;
using QuickNotes.Domain;

namespace QuickNotes.Infrastructure.Sqlite;

public sealed class SqliteCatalogRepository : ICatalogRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteCatalogRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task CreateCategoryAsync(Category category, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO categories (id, name, default_color, is_system)
            VALUES ($id, $name, $default_color, $is_system);
            """;
        command.Parameters.AddWithValue("$id", SqliteTypeMapping.GuidString(category.Id));
        command.Parameters.AddWithValue("$name", category.Name);
        command.Parameters.AddWithValue("$default_color", category.DefaultColor);
        command.Parameters.AddWithValue("$is_system", category.IsSystem ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CreateTagAsync(Tag tag, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tags (id, name)
            VALUES ($id, $name);
            """;
        command.Parameters.AddWithValue("$id", SqliteTypeMapping.GuidString(tag.Id));
        command.Parameters.AddWithValue("$name", tag.Name);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<Category?> GetCategoryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, default_color, is_system
            FROM categories
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", SqliteTypeMapping.GuidString(id));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new Category
        {
            Id = Guid.Parse(reader.GetString(0)),
            Name = reader.GetString(1),
            DefaultColor = reader.GetString(2),
            IsSystem = reader.GetInt64(3) != 0
        };
    }

    public async Task<Tag?> GetTagAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name
            FROM tags
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", SqliteTypeMapping.GuidString(id));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new Tag
        {
            Id = Guid.Parse(reader.GetString(0)),
            Name = reader.GetString(1)
        };
    }
}
