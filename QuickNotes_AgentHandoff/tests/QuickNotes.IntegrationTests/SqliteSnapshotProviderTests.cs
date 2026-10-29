using Microsoft.Data.Sqlite;
using QuickNotes.Application;
using QuickNotes.Domain;
using QuickNotes.Infrastructure.Sqlite;
using Xunit;

namespace QuickNotes.IntegrationTests;

public sealed class SqliteSnapshotProviderTests
{
    [Fact]
    public async Task CreateAsync_CopiesNotesLifecyclesTagsAndSchema()
    {
        using var database = await TestDatabase.CreateAsync();
        var tag = new Tag { Id = Guid.NewGuid(), Name = "backup-tag" };
        await database.Catalog.CreateTagAsync(tag);
        var active = TestData.NewNote();
        await database.Notes.CreateAsync(active, new[] { tag.Id });
        var archived = TestData.NewNote();
        await database.Notes.CreateAsync(archived, Array.Empty<Guid>());
        await database.Notes.SetLifecycleAsync(
            archived.Id,
            NoteLifecycle.Archived,
            1,
            archived.ModifiedAtUtc.AddMinutes(1));
        var trashed = TestData.NewNote();
        await database.Notes.CreateAsync(trashed, Array.Empty<Guid>());
        await database.Notes.SetLifecycleAsync(
            trashed.Id,
            NoteLifecycle.Trash,
            1,
            trashed.ModifiedAtUtc.AddMinutes(1));
        using var temporary = new TemporaryDirectory();
        var snapshotPath = Path.Combine(temporary.RootPath, "snapshot.db");
        var provider = new SqliteSnapshotProvider(database.DatabasePath);

        var info = await provider.CreateAsync(snapshotPath);

        Assert.Equal(2, info.SchemaVersion);
        Assert.True(info.HasChangeMarker);
        Assert.NotNull(info.Marker);
        Assert.Equal(2, info.Marker!.SchemaVersion);
        Assert.True(File.Exists(snapshotPath));
        Assert.False(File.Exists(snapshotPath + "-wal"));
        Assert.False(File.Exists(snapshotPath + "-shm"));
        await AssertSnapshotValidAsync(snapshotPath, 2);

        var snapshotNotes = new SqliteNoteRepository(new SqliteConnectionFactory(snapshotPath));
        Assert.Equal(active with { Version = 1 }, await snapshotNotes.GetAsync(active.Id));
        Assert.Equal(new[] { tag.Id }, await snapshotNotes.GetTagIdsAsync(active.Id));
        Assert.Equal(NoteLifecycle.Archived, (await snapshotNotes.GetAsync(archived.Id))!.Lifecycle);
        Assert.Equal(NoteLifecycle.Trash, (await snapshotNotes.GetAsync(trashed.Id))!.Lifecycle);

        await database.Notes.UpdateAsync(
            active with { Body = "source still writable", ModifiedAtUtc = active.ModifiedAtUtc.AddMinutes(5) },
            1,
            new[] { tag.Id });
        Assert.Equal("source still writable", (await database.Notes.GetAsync(active.Id))!.Body);
    }

    [Fact]
    public async Task CreateAsync_WithConcurrentWrites_KeepsSourceUsableAndSnapshotConsistent()
    {
        using var database = await TestDatabase.CreateAsync();
        var note = TestData.NewNote();
        await database.Notes.CreateAsync(note, Array.Empty<Guid>());
        using var temporary = new TemporaryDirectory();
        var snapshotPath = Path.Combine(temporary.RootPath, "snapshot.db");
        using var writerCancellation = new CancellationTokenSource();
        var writer = Task.Run(async () =>
        {
            var counter = 0;
            while (!writerCancellation.IsCancellationRequested)
            {
                try
                {
                    var current = await database.Notes.GetAsync(note.Id);
                    if (current is null)
                    {
                        return;
                    }

                    await database.Notes.UpdateAsync(
                        current with
                        {
                            Body = $"concurrent-{counter++}",
                            ModifiedAtUtc = current.ModifiedAtUtc.AddSeconds(1)
                        },
                        current.Version,
                        Array.Empty<Guid>());
                }
                catch (NoteConcurrencyException)
                {
                }
                catch (StorageUnavailableException)
                {
                }

                await Task.Delay(5);
            }
        });
        var provider = new SqliteSnapshotProvider(database.DatabasePath);
        try
        {
            await Task.Delay(20);
            var info = await provider.CreateAsync(snapshotPath);

            Assert.Equal(2, info.SchemaVersion);
            await AssertSnapshotValidAsync(snapshotPath, 2);
        }
        finally
        {
            writerCancellation.Cancel();
            await writer;
        }

        var current = (await database.Notes.GetAsync(note.Id))!;
        var updated = await database.Notes.UpdateAsync(
            current with
            {
                Body = "after concurrent backup",
                ModifiedAtUtc = current.ModifiedAtUtc.AddMinutes(1)
            },
            current.Version,
            Array.Empty<Guid>());
        Assert.Equal("after concurrent backup", updated.Body);
    }

    [Fact]
    public async Task CreateAsync_WhenSourceMissing_ThrowsSourceUnavailable()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = Path.Combine(temporary.RootPath, "missing.db");
        var snapshotPath = Path.Combine(temporary.RootPath, "snapshot.db");
        var provider = new SqliteSnapshotProvider(sourcePath);

        var exception = await Assert.ThrowsAsync<BackupException>(
            () => provider.CreateAsync(snapshotPath));

        Assert.Equal(BackupErrorCodes.SourceUnavailable, exception.ErrorCode);
        Assert.Equal("snapshot", exception.Operation);
        Assert.False(File.Exists(snapshotPath));
    }

    [Fact]
    public async Task CreateAsync_WhenDestinationExists_ThrowsAndKeepsExistingFile()
    {
        using var database = await TestDatabase.CreateAsync();
        using var temporary = new TemporaryDirectory();
        var destinationPath = Path.Combine(temporary.RootPath, "existing.db");
        await File.WriteAllTextAsync(destinationPath, "existing snapshot");
        var provider = new SqliteSnapshotProvider(database.DatabasePath);

        var exception = await Assert.ThrowsAsync<BackupException>(
            () => provider.CreateAsync(destinationPath));

        Assert.Equal(BackupErrorCodes.SnapshotFailed, exception.ErrorCode);
        Assert.Equal("existing snapshot", await File.ReadAllTextAsync(destinationPath));
    }

    [Fact]
    public async Task CreateAsync_WhenDestinationDirectoryMissing_ThrowsSnapshotFailed()
    {
        using var database = await TestDatabase.CreateAsync();
        using var temporary = new TemporaryDirectory();
        var snapshotPath = Path.Combine(temporary.RootPath, "missing", "snapshot.db");
        var provider = new SqliteSnapshotProvider(database.DatabasePath);

        var exception = await Assert.ThrowsAsync<BackupException>(
            () => provider.CreateAsync(snapshotPath));

        Assert.Equal(BackupErrorCodes.SnapshotFailed, exception.ErrorCode);
        Assert.False(File.Exists(snapshotPath));
    }

    [Fact]
    public async Task CreateAsync_WhenCancelledBeforeStart_ThrowsOperationCanceled()
    {
        using var database = await TestDatabase.CreateAsync();
        using var temporary = new TemporaryDirectory();
        var snapshotPath = Path.Combine(temporary.RootPath, "snapshot.db");
        var provider = new SqliteSnapshotProvider(database.DatabasePath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.CreateAsync(snapshotPath, cancellation.Token));

        Assert.False(File.Exists(snapshotPath));
    }

    [Fact]
    public async Task CreateAsync_WhenSchemaVersionIsZero_ThrowsSnapshotVerificationFailed()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = Path.Combine(temporary.RootPath, "empty.db");
        await CreateEmptyDatabaseAsync(sourcePath);
        var snapshotPath = Path.Combine(temporary.RootPath, "snapshot.db");
        var provider = new SqliteSnapshotProvider(sourcePath);

        var exception = await Assert.ThrowsAsync<BackupException>(
            () => provider.CreateAsync(snapshotPath));

        Assert.Equal(BackupErrorCodes.SnapshotVerificationFailed, exception.ErrorCode);
        Assert.False(File.Exists(snapshotPath));
    }

    [Fact]
    public async Task CreateAsync_SchemaTwo_ReturnsChangeMarkerFromSnapshot()
    {
        using var database = await TestDatabase.CreateAsync();
        var (databaseId, changeSequence) = await ReadMetaAsync(database.DatabasePath);
        using var temporary = new TemporaryDirectory();
        var snapshotPath = Path.Combine(temporary.RootPath, "snapshot.db");
        var provider = new SqliteSnapshotProvider(database.DatabasePath);

        var info = await provider.CreateAsync(snapshotPath);

        var expected = new DataChangeMarker(2, databaseId, changeSequence);
        Assert.Equal(2, info.SchemaVersion);
        Assert.Equal(expected, info.Marker);
        Assert.Equal(expected.ToFingerprint(), info.ToFingerprint());
        var snapshotMeta = await ReadMetaAsync(snapshotPath);
        Assert.Equal((databaseId, changeSequence), snapshotMeta);
    }

    [Fact]
    public async Task CreateAsync_SchemaTwoWithoutDatabaseMeta_ThrowsChangeMarkerUnavailable()
    {
        using var database = await TestDatabase.CreateAsync();
        using var temporary = new TemporaryDirectory();
        var snapshotPath = Path.Combine(temporary.RootPath, "snapshot.db");
        await ExecuteAsync(database.DatabasePath, "DROP TABLE database_meta;");
        var provider = new SqliteSnapshotProvider(database.DatabasePath);

        var exception = await Assert.ThrowsAsync<BackupException>(
            () => provider.CreateAsync(snapshotPath));

        Assert.Equal(BackupErrorCodes.ChangeMarkerUnavailable, exception.ErrorCode);
        Assert.Equal("snapshot", exception.Operation);
        Assert.False(File.Exists(snapshotPath));
    }

    [Fact]
    public async Task CreateAsync_SchemaTwoWithoutDatabaseMetaRow_ThrowsChangeMarkerUnavailable()
    {
        using var database = await TestDatabase.CreateAsync();
        using var temporary = new TemporaryDirectory();
        var snapshotPath = Path.Combine(temporary.RootPath, "snapshot.db");
        await ExecuteAsync(database.DatabasePath, "DELETE FROM database_meta;");
        var provider = new SqliteSnapshotProvider(database.DatabasePath);

        var exception = await Assert.ThrowsAsync<BackupException>(
            () => provider.CreateAsync(snapshotPath));

        Assert.Equal(BackupErrorCodes.ChangeMarkerUnavailable, exception.ErrorCode);
        Assert.False(File.Exists(snapshotPath));
    }

    [Fact]
    public async Task CreateAsync_SchemaOneProtectionSnapshot_ReturnsNoChangeMarker()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = Path.Combine(temporary.RootPath, "v1.db");
        var snapshotPath = Path.Combine(temporary.RootPath, "snapshot.db");
        await TestDatabase.CreateVersionOneAsync(sourcePath);
        var provider = new SqliteSnapshotProvider(sourcePath);

        var info = await provider.CreateAsync(snapshotPath);

        Assert.Equal(1, info.SchemaVersion);
        Assert.Null(info.Marker);
        Assert.False(info.HasChangeMarker);
        Assert.Null(info.ToFingerprint());
        await AssertSnapshotValidAsync(snapshotPath, 1);
    }

    private static async Task<(Guid DatabaseId, long ChangeSequence)> ReadMetaAsync(
        string databasePath)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT database_id, change_sequence
            FROM database_meta
            WHERE id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (Guid.Parse(reader.GetString(0)), reader.GetInt64(1));
    }

    private static async Task ExecuteAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={databasePath};Mode=ReadWrite;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CreateEmptyDatabaseAsync(string path)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={path};Mode=ReadWriteCreate;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version=0;";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertSnapshotValidAsync(string path, long expectedSchemaVersion)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={path};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA integrity_check;";
            Assert.Equal("ok", Convert.ToString(await command.ExecuteScalarAsync()));
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version;";
            Assert.Equal(expectedSchemaVersion, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_key_check;";
            await using var reader = await command.ExecuteReaderAsync();
            Assert.False(await reader.ReadAsync());
        }
    }
}
