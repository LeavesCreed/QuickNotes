using System.Data;
using System.Globalization;
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

    public async Task CreateCategoryAsync(
        Category category,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(category);
        const string operation = "create_category";
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        try
        {
            await using var transaction = BeginWriteTransaction(connection);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
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

            await BumpAndCommitAsync(connection, transaction, cancellationToken);
        }
        catch (SqliteException exception)
        {
            throw SqliteErrorTranslator.Translate(
                exception,
                operation,
                noteId: null,
                cancellationToken);
        }
    }

    public async Task UpdateCategoryAsync(
        Guid id,
        string name,
        string defaultColor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(defaultColor);
        const string operation = "update_category";
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        try
        {
            await using var transaction = BeginWriteTransaction(connection);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE categories
                    SET name = $name,
                        default_color = $default_color
                    WHERE id = $id;
                    """;
                command.Parameters.AddWithValue("$id", SqliteTypeMapping.GuidString(id));
                command.Parameters.AddWithValue("$name", name);
                command.Parameters.AddWithValue("$default_color", defaultColor);
                var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken);
                if (affectedRows == 0)
                {
                    throw new NoteNotFoundException(id, operation);
                }
            }

            await BumpAndCommitAsync(connection, transaction, cancellationToken);
        }
        catch (SqliteException exception)
        {
            throw SqliteErrorTranslator.Translate(
                exception,
                operation,
                noteId: null,
                cancellationToken);
        }
    }

    public async Task DeleteCategoryAsync(
        Guid id,
        Guid reassignCategoryId,
        CancellationToken cancellationToken = default)
    {
        const string operation = "delete_category";
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        try
        {
            await using var transaction = BeginWriteTransaction(connection);
            var isSystem = await ReadCategoryIsSystemAsync(
                connection,
                transaction,
                id,
                cancellationToken);
            if (isSystem is null)
            {
                throw new NoteNotFoundException(id, operation);
            }

            if (isSystem.Value)
            {
                throw new InvalidOperationException(
                    $"Category {id} is a system category and cannot be deleted.");
            }

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE notes
                    SET category_id = $reassign_category_id,
                        version = version + 1,
                        modified_at_utc = $modified_at_utc
                    WHERE category_id = $id;
                    """;
                command.Parameters.AddWithValue("$reassign_category_id", SqliteTypeMapping.GuidString(reassignCategoryId));
                command.Parameters.AddWithValue("$modified_at_utc", SqliteTypeMapping.UtcString(DateTimeOffset.UtcNow));
                command.Parameters.AddWithValue("$id", SqliteTypeMapping.GuidString(id));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM categories WHERE id = $id AND is_system = 0;";
                command.Parameters.AddWithValue("$id", SqliteTypeMapping.GuidString(id));
                var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken);
                if (affectedRows == 0)
                {
                    throw new NoteNotFoundException(id, operation);
                }
            }

            await BumpAndCommitAsync(connection, transaction, cancellationToken);
        }
        catch (SqliteException exception)
        {
            throw SqliteErrorTranslator.Translate(
                exception,
                operation,
                noteId: null,
                cancellationToken);
        }
    }

    public async Task CreateTagAsync(
        Tag tag,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tag);
        const string operation = "create_tag";
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        try
        {
            await using var transaction = BeginWriteTransaction(connection);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO tags (id, name)
                    VALUES ($id, $name);
                    """;
                command.Parameters.AddWithValue("$id", SqliteTypeMapping.GuidString(tag.Id));
                command.Parameters.AddWithValue("$name", tag.Name);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await BumpAndCommitAsync(connection, transaction, cancellationToken);
        }
        catch (SqliteException exception)
        {
            throw SqliteErrorTranslator.Translate(
                exception,
                operation,
                noteId: null,
                cancellationToken);
        }
    }

    public async Task UpdateTagAsync(
        Guid id,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        const string operation = "update_tag";
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        try
        {
            await using var transaction = BeginWriteTransaction(connection);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE tags
                    SET name = $name
                    WHERE id = $id;
                    """;
                command.Parameters.AddWithValue("$id", SqliteTypeMapping.GuidString(id));
                command.Parameters.AddWithValue("$name", name);
                var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken);
                if (affectedRows == 0)
                {
                    throw new NoteNotFoundException(id, operation);
                }
            }

            await BumpAndCommitAsync(connection, transaction, cancellationToken);
        }
        catch (SqliteException exception)
        {
            throw SqliteErrorTranslator.Translate(
                exception,
                operation,
                noteId: null,
                cancellationToken);
        }
    }

    public async Task DeleteTagAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        const string operation = "delete_tag";
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        try
        {
            await using var transaction = BeginWriteTransaction(connection);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM tags WHERE id = $id;";
                command.Parameters.AddWithValue("$id", SqliteTypeMapping.GuidString(id));
                var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken);
                if (affectedRows == 0)
                {
                    throw new NoteNotFoundException(id, operation);
                }
            }

            await BumpAndCommitAsync(connection, transaction, cancellationToken);
        }
        catch (SqliteException exception)
        {
            throw SqliteErrorTranslator.Translate(
                exception,
                operation,
                noteId: null,
                cancellationToken);
        }
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

    public async Task<IReadOnlyList<Category>> ListCategoriesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, default_color, is_system
            FROM categories
            ORDER BY is_system DESC, name COLLATE NOCASE ASC;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var categories = new List<Category>();
        while (await reader.ReadAsync(cancellationToken))
        {
            categories.Add(new Category
            {
                Id = Guid.Parse(reader.GetString(0)),
                Name = reader.GetString(1),
                DefaultColor = reader.GetString(2),
                IsSystem = reader.GetInt64(3) != 0
            });
        }

        return categories.AsReadOnly();
    }

    public async Task<IReadOnlyList<Tag>> ListTagsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name
            FROM tags
            ORDER BY name COLLATE NOCASE ASC;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tags = new List<Tag>();
        while (await reader.ReadAsync(cancellationToken))
        {
            tags.Add(new Tag
            {
                Id = Guid.Parse(reader.GetString(0)),
                Name = reader.GetString(1)
            });
        }

        return tags.AsReadOnly();
    }

    public async Task MergeTagAsync(
        Guid sourceTagId,
        Guid targetTagId,
        CancellationToken cancellationToken = default)
    {
        if (sourceTagId == Guid.Empty || targetTagId == Guid.Empty)
        {
            throw new ArgumentException("Tag ids must not be empty.");
        }

        if (sourceTagId == targetTagId)
        {
            throw new ArgumentException("Source and target tags must differ.", nameof(targetTagId));
        }

        const string operation = "merge_tag";
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        try
        {
            await using var transaction = BeginWriteTransaction(connection);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT OR IGNORE INTO note_tags (note_id, tag_id)
                    SELECT note_id, $target_tag_id
                    FROM note_tags
                    WHERE tag_id = $source_tag_id;
                    """;
                command.Parameters.AddWithValue("$source_tag_id", SqliteTypeMapping.GuidString(sourceTagId));
                command.Parameters.AddWithValue("$target_tag_id", SqliteTypeMapping.GuidString(targetTagId));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM note_tags WHERE tag_id = $source_tag_id;";
                command.Parameters.AddWithValue("$source_tag_id", SqliteTypeMapping.GuidString(sourceTagId));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM tags WHERE id = $source_tag_id;";
                command.Parameters.AddWithValue("$source_tag_id", SqliteTypeMapping.GuidString(sourceTagId));
                var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken);
                if (affectedRows == 0)
                {
                    throw new CatalogNotFoundException(sourceTagId, operation);
                }
            }

            await BumpAndCommitAsync(connection, transaction, cancellationToken);
        }
        catch (SqliteException exception)
        {
            throw SqliteErrorTranslator.Translate(exception, operation, noteId: null, cancellationToken);
        }
    }

    private static SqliteTransaction BeginWriteTransaction(SqliteConnection connection) =>
        connection.BeginTransaction(IsolationLevel.Serializable, deferred: true);

    private static async Task BumpAndCommitAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await SqliteChangeMarker.BumpAsync(connection, transaction, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
    }

    private static async Task<bool?> ReadCategoryIsSystemAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT is_system FROM categories WHERE id = $id;";
        command.Parameters.AddWithValue("$id", SqliteTypeMapping.GuidString(id));
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null or DBNull)
        {
            return null;
        }

        return Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0;
    }
}
