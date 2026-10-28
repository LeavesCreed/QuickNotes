using System.Data;
using System.Globalization;
using System.Runtime.ExceptionServices;
using Microsoft.Data.Sqlite;
using QuickNotes.Application;
using QuickNotes.Domain;

namespace QuickNotes.Infrastructure.Sqlite;

public sealed class SqliteNoteRepository : INoteRepository
{
    private const string NoteColumns = "id, title, body, created_at_utc, modified_at_utc, version, lifecycle, category_id, is_pinned, display_intent, color_mode, color_hex, font_size, window_left, window_top, window_width, window_height";

    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteNoteRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<Note> CreateAsync(
        Note note,
        IReadOnlyCollection<Guid> tagIds,
        CancellationToken cancellationToken = default)
    {
        const string operation = "create";
        ValidateAppearance(note);
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        SqliteTransaction? transaction = null;
        try
        {
            transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: true);

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"""
                    INSERT INTO notes ({NoteColumns})
                    VALUES ($id, $title, $body, $created_at_utc, $modified_at_utc, 1, 0, $category_id, $is_pinned, $display_intent, $color_mode, $color_hex, $font_size, $window_left, $window_top, $window_width, $window_height);
                    """;
                command.Parameters.AddWithValue("$id", SqliteTypeMapping.GuidString(note.Id));
                command.Parameters.AddWithValue("$title", (object?)note.Title ?? DBNull.Value);
                command.Parameters.AddWithValue("$body", note.Body);
                command.Parameters.AddWithValue("$created_at_utc", SqliteTypeMapping.UtcString(note.CreatedAtUtc));
                command.Parameters.AddWithValue("$modified_at_utc", SqliteTypeMapping.UtcString(note.ModifiedAtUtc));
                command.Parameters.AddWithValue("$category_id", SqliteTypeMapping.GuidString(note.CategoryId));
                command.Parameters.AddWithValue("$is_pinned", note.IsPinned ? 1 : 0);
                command.Parameters.AddWithValue("$display_intent", (int)note.DisplayIntent);
                AddAppearanceParameters(command, note);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertNoteTagsAsync(connection, transaction, note.Id, tagIds, cancellationToken);

            var created = await ReadNoteAsync(connection, transaction, note.Id, cancellationToken);
            if (created is null)
            {
                throw new InvalidOperationException($"Note {note.Id} disappeared during create.");
            }

            await SqliteChangeMarker.BumpAsync(connection, transaction, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await CommitAsync(transaction, operation, note.Id, expectedVersion: 0);
            await DisposeTransactionQuietlyAsync(transaction);
            return created;
        }
        catch (SqliteException exception)
        {
            var translated = SqliteErrorTranslator.Translate(exception, operation, note.Id, cancellationToken);
            if (transaction is not null)
            {
                await DisposeTransactionOnFailureAsync(transaction, translated);
            }

            ExceptionDispatchInfo.Capture(translated).Throw();
            throw;
        }
        catch (Exception exception)
        {
            if (transaction is not null)
            {
                await DisposeTransactionOnFailureAsync(transaction, exception);
            }

            throw;
        }
    }

    public async Task<Note?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        const string operation = "get";
        try
        {
            await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT {NoteColumns}
                FROM notes
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", SqliteTypeMapping.GuidString(id));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? SqliteTypeMapping.ReadNote(reader) : null;
        }
        catch (SqliteException exception)
        {
            throw SqliteErrorTranslator.Translate(exception, operation, id, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<Note>> ListAsync(
        NoteLifecycle lifecycle,
        CancellationToken cancellationToken = default)
    {
        const string operation = "list";
        try
        {
            await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT {NoteColumns}
                FROM notes
                WHERE lifecycle = $lifecycle
                ORDER BY modified_at_utc DESC, id;
                """;
            command.Parameters.AddWithValue("$lifecycle", (int)lifecycle);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var notes = new List<Note>();
            while (await reader.ReadAsync(cancellationToken))
            {
                notes.Add(SqliteTypeMapping.ReadNote(reader));
            }

            return notes.AsReadOnly();
        }
        catch (SqliteException exception)
        {
            throw SqliteErrorTranslator.Translate(exception, operation, noteId: null, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<Guid>> GetTagIdsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        const string operation = "get_tags";
        try
        {
            await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT tag_id
                FROM note_tags
                WHERE note_id = $note_id
                ORDER BY tag_id;
                """;
            command.Parameters.AddWithValue("$note_id", SqliteTypeMapping.GuidString(id));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var tagIds = new List<Guid>();
            while (await reader.ReadAsync(cancellationToken))
            {
                tagIds.Add(Guid.Parse(reader.GetString(0)));
            }

            return tagIds.AsReadOnly();
        }
        catch (SqliteException exception)
        {
            throw SqliteErrorTranslator.Translate(exception, operation, id, cancellationToken);
        }
    }

    public async Task<Note> UpdateAsync(
        Note note,
        long expectedVersion,
        IReadOnlyCollection<Guid> tagIds,
        CancellationToken cancellationToken = default)
    {
        const string operation = "update";
        ValidateAppearance(note);
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        SqliteTransaction? transaction = null;
        try
        {
            transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: true);

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE notes
                    SET title = $title,
                        body = $body,
                        modified_at_utc = $modified_at_utc,
                        category_id = $category_id,
                        is_pinned = $is_pinned,
                        display_intent = $display_intent,
                        color_mode = $color_mode,
                        color_hex = $color_hex,
                        font_size = $font_size,
                        window_left = $window_left,
                        window_top = $window_top,
                        window_width = $window_width,
                        window_height = $window_height,
                        version = version + 1
                    WHERE id = $id AND version = $expected_version;
                    """;
                command.Parameters.AddWithValue("$id", SqliteTypeMapping.GuidString(note.Id));
                command.Parameters.AddWithValue("$expected_version", expectedVersion);
                command.Parameters.AddWithValue("$title", (object?)note.Title ?? DBNull.Value);
                command.Parameters.AddWithValue("$body", note.Body);
                command.Parameters.AddWithValue("$modified_at_utc", SqliteTypeMapping.UtcString(note.ModifiedAtUtc));
                command.Parameters.AddWithValue("$category_id", SqliteTypeMapping.GuidString(note.CategoryId));
                command.Parameters.AddWithValue("$is_pinned", note.IsPinned ? 1 : 0);
                command.Parameters.AddWithValue("$display_intent", (int)note.DisplayIntent);
                AddAppearanceParameters(command, note);
                var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken);
                if (affectedRows == 0)
                {
                    throw await CreateVersionFailureAsync(
                        connection, transaction, note.Id, expectedVersion, operation, cancellationToken);
                }
            }

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM note_tags WHERE note_id = $note_id;";
                command.Parameters.AddWithValue("$note_id", SqliteTypeMapping.GuidString(note.Id));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertNoteTagsAsync(connection, transaction, note.Id, tagIds, cancellationToken);

            var updated = await ReadNoteAsync(connection, transaction, note.Id, cancellationToken);
            if (updated is null)
            {
                throw new InvalidOperationException($"Note {note.Id} disappeared during update.");
            }

            await SqliteChangeMarker.BumpAsync(connection, transaction, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await CommitAsync(transaction, operation, note.Id, expectedVersion);
            await DisposeTransactionQuietlyAsync(transaction);
            return updated;
        }
        catch (SqliteException exception)
        {
            var translated = SqliteErrorTranslator.Translate(exception, operation, note.Id, cancellationToken);
            if (transaction is not null)
            {
                await DisposeTransactionOnFailureAsync(transaction, translated);
            }

            ExceptionDispatchInfo.Capture(translated).Throw();
            throw;
        }
        catch (Exception exception)
        {
            if (transaction is not null)
            {
                await DisposeTransactionOnFailureAsync(transaction, exception);
            }

            throw;
        }
    }

    public async Task<Note> SetLifecycleAsync(
        Guid id,
        NoteLifecycle lifecycle,
        long expectedVersion,
        DateTimeOffset modifiedAtUtc,
        CancellationToken cancellationToken = default)
    {
        const string operation = "set_lifecycle";
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        SqliteTransaction? transaction = null;
        try
        {
            transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: true);

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE notes
                    SET lifecycle = $lifecycle,
                        display_intent = 0,
                        modified_at_utc = $modified_at_utc,
                        version = version + 1
                    WHERE id = $id AND version = $expected_version;
                    """;
                command.Parameters.AddWithValue("$id", SqliteTypeMapping.GuidString(id));
                command.Parameters.AddWithValue("$expected_version", expectedVersion);
                command.Parameters.AddWithValue("$lifecycle", (int)lifecycle);
                command.Parameters.AddWithValue("$modified_at_utc", SqliteTypeMapping.UtcString(modifiedAtUtc));
                var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken);
                if (affectedRows == 0)
                {
                    throw await CreateVersionFailureAsync(
                        connection, transaction, id, expectedVersion, operation, cancellationToken);
                }
            }

            var updated = await ReadNoteAsync(connection, transaction, id, cancellationToken);
            if (updated is null)
            {
                throw new InvalidOperationException($"Note {id} disappeared during lifecycle update.");
            }

            await SqliteChangeMarker.BumpAsync(connection, transaction, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await CommitAsync(transaction, operation, id, expectedVersion);
            await DisposeTransactionQuietlyAsync(transaction);
            return updated;
        }
        catch (SqliteException exception)
        {
            var translated = SqliteErrorTranslator.Translate(exception, operation, id, cancellationToken);
            if (transaction is not null)
            {
                await DisposeTransactionOnFailureAsync(transaction, translated);
            }

            ExceptionDispatchInfo.Capture(translated).Throw();
            throw;
        }
        catch (Exception exception)
        {
            if (transaction is not null)
            {
                await DisposeTransactionOnFailureAsync(transaction, exception);
            }

            throw;
        }
    }

    private static void ValidateAppearance(Note note)
    {
        NoteRules.ValidateColor(note.ColorMode, note.ColorHex);
        NoteRules.ValidateFontSize(note.FontSize);
        NoteRules.ValidateWindowLayout(note.WindowLayout);
    }

    private static void AddAppearanceParameters(SqliteCommand command, Note note)
    {
        command.Parameters.AddWithValue("$color_mode", (int)note.ColorMode);
        command.Parameters.AddWithValue("$color_hex", (object?)note.ColorHex ?? DBNull.Value);
        command.Parameters.AddWithValue("$font_size", (object?)note.FontSize ?? DBNull.Value);
        command.Parameters.AddWithValue("$window_left", (object?)note.WindowLayout?.Left ?? DBNull.Value);
        command.Parameters.AddWithValue("$window_top", (object?)note.WindowLayout?.Top ?? DBNull.Value);
        command.Parameters.AddWithValue("$window_width", (object?)note.WindowLayout?.Width ?? DBNull.Value);
        command.Parameters.AddWithValue("$window_height", (object?)note.WindowLayout?.Height ?? DBNull.Value);
    }

    private static async Task CommitAsync(
        SqliteTransaction transaction,
        string operation,
        Guid noteId,
        long expectedVersion)
    {
        try
        {
            await transaction.CommitAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            throw new SaveOutcomeUnknownException(operation, noteId, expectedVersion, exception);
        }
    }

    private static async Task DisposeTransactionQuietlyAsync(SqliteTransaction? transaction)
    {
        if (transaction is null)
        {
            return;
        }

        try
        {
            await transaction.DisposeAsync();
        }
        catch
        {
            // Disposal after a completed commit must not change the saved outcome.
        }
    }

    private static async Task DisposeTransactionOnFailureAsync(
        SqliteTransaction transaction,
        Exception failure)
    {
        try
        {
            await transaction.DisposeAsync();
        }
        catch (Exception rollbackException)
        {
            failure.Data["rollback_error"] = rollbackException is SqliteException sqliteException
                ? $"SqliteException:{sqliteException.SqliteExtendedErrorCode}"
                : rollbackException.GetType().Name;
        }
    }

    private static async Task InsertNoteTagsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid noteId,
        IEnumerable<Guid> tagIds,
        CancellationToken cancellationToken)
    {
        foreach (var tagId in tagIds.Distinct())
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO note_tags (note_id, tag_id)
                VALUES ($note_id, $tag_id);
                """;
            command.Parameters.AddWithValue("$note_id", SqliteTypeMapping.GuidString(noteId));
            command.Parameters.AddWithValue("$tag_id", SqliteTypeMapping.GuidString(tagId));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<Note?> ReadNoteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {NoteColumns}
            FROM notes
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", SqliteTypeMapping.GuidString(id));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? SqliteTypeMapping.ReadNote(reader) : null;
    }

    private static async Task<Exception> CreateVersionFailureAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid id,
        long expectedVersion,
        string operation,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT version FROM notes WHERE id = $id;";
        command.Parameters.AddWithValue("$id", SqliteTypeMapping.GuidString(id));
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null or DBNull)
        {
            return new NoteNotFoundException(id, operation);
        }

        var actualVersion = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        return new NoteConcurrencyException(id, expectedVersion, actualVersion, operation);
    }
}
