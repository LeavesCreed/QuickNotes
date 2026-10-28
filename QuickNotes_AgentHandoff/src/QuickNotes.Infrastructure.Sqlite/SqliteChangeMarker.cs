using Microsoft.Data.Sqlite;

namespace QuickNotes.Infrastructure.Sqlite;

public static class SqliteChangeMarker
{
    public static async Task<(Guid DatabaseId, long ChangeSequence)> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT database_id, change_sequence
            FROM database_meta
            WHERE id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("The database_meta row is missing.");
        }

        return (Guid.Parse(reader.GetString(0)), reader.GetInt64(1));
    }

    public static async Task BumpAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE database_meta
            SET change_sequence = change_sequence + 1
            WHERE id = 1;
            """;
        var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affectedRows != 1)
        {
            throw new InvalidOperationException("The database_meta row is missing.");
        }
    }
}
