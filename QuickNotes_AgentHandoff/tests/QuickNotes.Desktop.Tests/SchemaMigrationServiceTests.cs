using QuickNotes.Application;
using QuickNotes.Desktop.Diagnostics;
using QuickNotes.Desktop.Migration;
using QuickNotes.Desktop.Paths;
using QuickNotes.Domain;
using QuickNotes.Infrastructure.Sqlite;

namespace QuickNotes.Desktop.Tests;

public sealed class SchemaMigrationServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 28, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task EnsureCurrent_WhenV1_MigratesAndRetainsProtectionSnapshot()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(Path.Combine(temporary.RootPath, "data"));
        paths.EnsureDirectories();
        await SchemaMigrationTestDatabase.CreateVersionOneAsync(paths.DatabasePath);
        var service = CreateService(paths, new SqliteSchemaMigrator());

        var result = await service.EnsureCurrentAsync(1);

        Assert.Equal(SchemaMigrationStatus.Migrated, result.Status);
        Assert.Equal(2, result.SchemaVersion);
        Assert.Null(result.ErrorCode);
        Assert.NotNull(result.ProtectionSnapshotPath);
        Assert.True(File.Exists(result.ProtectionSnapshotPath));
        Assert.Matches(
            @"^notes-v1-\d{8}-\d{6}-\d{3}\.db$",
            Path.GetFileName(result.ProtectionSnapshotPath!));
        var protectedInfo = await new SqliteRestoreStore().InspectAsync(result.ProtectionSnapshotPath!);
        Assert.Equal(1, protectedInfo.SchemaVersion);
        Assert.Equal(2, await SchemaMigrationTestDatabase.ReadUserVersionAsync(paths.DatabasePath));
        Assert.Equal(
            1,
            await SchemaMigrationTestDatabase.ScalarLongAsync(
                paths.DatabasePath,
                "SELECT COUNT(*) FROM database_meta;"));
        Assert.True(Directory.Exists(paths.MigrationProtectionDirectory));
        Assert.Single(Directory.GetFiles(paths.MigrationProtectionDirectory, "*.db"));
    }

    [Fact]
    public async Task EnsureCurrent_WhenAlreadyCurrent_DoesNotMigrateOrCreateSnapshot()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(Path.Combine(temporary.RootPath, "data"));
        paths.EnsureDirectories();
        await new SqliteSchema(new SqliteConnectionFactory(paths.DatabasePath)).InitializeAsync();
        var migrator = new RecordingSchemaMigrator();
        var service = CreateService(paths, migrator);

        var result = await service.EnsureCurrentAsync(2);

        Assert.Equal(SchemaMigrationStatus.AlreadyCurrent, result.Status);
        Assert.Equal(2, result.SchemaVersion);
        Assert.Null(result.ProtectionSnapshotPath);
        Assert.Null(result.ErrorCode);
        Assert.Equal(0, migrator.Calls);
        Assert.False(Directory.Exists(paths.MigrationProtectionDirectory));
    }

    [Fact]
    public async Task EnsureCurrent_WhenUnsupported_DoesNotMigrateOrCreateSnapshot()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(Path.Combine(temporary.RootPath, "data"));
        paths.EnsureDirectories();
        var migrator = new RecordingSchemaMigrator();
        var service = CreateService(paths, migrator);

        var result = await service.EnsureCurrentAsync(3);

        Assert.Equal(SchemaMigrationStatus.Unsupported, result.Status);
        Assert.Equal(3, result.SchemaVersion);
        Assert.Equal("schema_unsupported", result.ErrorCode);
        Assert.Null(result.ProtectionSnapshotPath);
        Assert.Equal(0, migrator.Calls);
        Assert.False(Directory.Exists(paths.MigrationProtectionDirectory));
    }

    [Fact]
    public async Task EnsureCurrent_WhenSourceDatabaseMissing_ReturnsFailedWithoutMigrating()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(Path.Combine(temporary.RootPath, "data"));
        paths.EnsureDirectories();
        var migrator = new RecordingSchemaMigrator();
        var service = CreateService(paths, migrator);

        var result = await service.EnsureCurrentAsync(1);

        Assert.Equal(SchemaMigrationStatus.Failed, result.Status);
        Assert.Equal(1, result.SchemaVersion);
        Assert.Equal("migration_failed", result.ErrorCode);
        Assert.NotNull(result.ProtectionSnapshotPath);
        Assert.False(File.Exists(paths.DatabasePath));
        Assert.Equal(0, migrator.Calls);
    }

    [Fact]
    public async Task EnsureCurrent_WhenMigratorFails_ReturnsFailedAndKeepsProtectionSnapshot()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(Path.Combine(temporary.RootPath, "data"));
        paths.EnsureDirectories();
        await SchemaMigrationTestDatabase.CreateVersionOneAsync(paths.DatabasePath);
        var service = CreateService(paths, new ThrowingSchemaMigrator());

        var result = await service.EnsureCurrentAsync(1);

        Assert.Equal(SchemaMigrationStatus.Failed, result.Status);
        Assert.Equal(1, result.SchemaVersion);
        Assert.Equal("migration_failed", result.ErrorCode);
        Assert.NotNull(result.ProtectionSnapshotPath);
        Assert.True(File.Exists(result.ProtectionSnapshotPath));
        var protectedInfo = await new SqliteRestoreStore().InspectAsync(result.ProtectionSnapshotPath!);
        Assert.Equal(1, protectedInfo.SchemaVersion);
        Assert.Equal(1, await SchemaMigrationTestDatabase.ReadUserVersionAsync(paths.DatabasePath));
        Assert.Equal(
            0,
            await SchemaMigrationTestDatabase.ScalarLongAsync(
                paths.DatabasePath,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'database_meta';"));
    }

    [Fact]
    public async Task EnsureCurrent_WhenRealMigrationFails_ReturnsFailedAndKeepsOriginalV1()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(Path.Combine(temporary.RootPath, "data"));
        paths.EnsureDirectories();
        await SchemaMigrationTestDatabase.CreateVersionOneAsync(paths.DatabasePath);
        await SchemaMigrationTestDatabase.ExecuteAsync(
            paths.DatabasePath,
            "CREATE TABLE database_meta (id INTEGER PRIMARY KEY, legacy TEXT);");
        var service = CreateService(paths, new SqliteSchemaMigrator());

        var result = await service.EnsureCurrentAsync(1);

        Assert.Equal(SchemaMigrationStatus.Failed, result.Status);
        Assert.Equal("migration_failed", result.ErrorCode);
        Assert.Contains("SQLite error", result.Message);
        Assert.Contains("database_meta", result.Message);
        Assert.NotNull(result.ProtectionSnapshotPath);
        Assert.True(File.Exists(result.ProtectionSnapshotPath));
        Assert.Equal(1, await SchemaMigrationTestDatabase.ReadUserVersionAsync(paths.DatabasePath));
        Assert.Equal(
            0,
            await SchemaMigrationTestDatabase.ScalarLongAsync(
                paths.DatabasePath,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'app_settings';"));
        var logText = File.ReadAllText(
            Path.Combine(paths.LogDirectory, "quicknotes-20261028.log"));
        Assert.Contains("ERROR | migration | migration_failed", logText);
        Assert.Contains("database_meta", logText);
    }

    [Fact]
    public async Task EnsureCurrent_WhenMigratorThrowsSchemaMigrationException_PreservesDetailInResultAndLog()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(Path.Combine(temporary.RootPath, "data"));
        paths.EnsureDirectories();
        await SchemaMigrationTestDatabase.CreateVersionOneAsync(paths.DatabasePath);
        var service = CreateService(paths, new DetailedFailureSchemaMigrator());

        var result = await service.EnsureCurrentAsync(1);

        Assert.Equal(SchemaMigrationStatus.Failed, result.Status);
        Assert.Equal("migration_failed", result.ErrorCode);
        Assert.Equal(1, result.SchemaVersion);
        Assert.Contains("migration_verification_failed", result.Message);
        Assert.Contains("database disk image is malformed", result.Message);

        var logText = File.ReadAllText(
            Path.Combine(paths.LogDirectory, "quicknotes-20261028.log"));
        Assert.Contains("ERROR | migration | migration_verification_failed", logText);
        Assert.Contains("database disk image is malformed", logText);
    }

    private static SchemaMigrationService CreateService(
        DesktopPaths paths,
        ISchemaMigrator migrator)
    {
        var time = new TestTimeProvider(Now);
        return new SchemaMigrationService(
            paths,
            migrator,
            new SqliteRestoreStore(),
            new DiagnosticLog(paths.LogDirectory, time),
            time);
    }
}

internal sealed class RecordingSchemaMigrator : ISchemaMigrator
{
    public int Calls { get; private set; }

    public Task<SchemaMigrationOutcome> MigrateToCurrentAsync(
        string databasePath,
        bool rotateIdentity,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(
            new SchemaMigrationOutcome(1, 2, Guid.NewGuid(), Migrated: true, IdentityRotated: false));
    }
}

internal sealed class ThrowingSchemaMigrator : ISchemaMigrator
{
    public Task<SchemaMigrationOutcome> MigrateToCurrentAsync(
        string databasePath,
        bool rotateIdentity,
        CancellationToken cancellationToken = default) =>
        Task.FromException<SchemaMigrationOutcome>(
            new InvalidOperationException("simulated migration failure"));
}

internal sealed class DetailedFailureSchemaMigrator : ISchemaMigrator
{
    public Task<SchemaMigrationOutcome> MigrateToCurrentAsync(
        string databasePath,
        bool rotateIdentity,
        CancellationToken cancellationToken = default) =>
        Task.FromException<SchemaMigrationOutcome>(
            new SchemaMigrationException(
                "migration_verification_failed",
                "The migrated database failed integrity_check.",
                new InvalidOperationException("simulated SQLite detail: database disk image is malformed")));
}

internal static class SchemaMigrationTestDatabase
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

    public static async Task CreateVersionOneAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var factory = new SqliteConnectionFactory(fullPath);
        await using var connection = await factory.OpenAsync(cancellationToken);
        await ExecuteAsync(connection, VersionOneDdl, cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO categories (id, name, default_color, is_system)
                VALUES ($id, $name, $default_color, 1);
                """;
            command.Parameters.AddWithValue("$id", Category.UncategorizedId.ToString("D"));
            command.Parameters.AddWithValue("$name", "未分类");
            command.Parameters.AddWithValue("$default_color", "#E6E6FA");
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await ExecuteAsync(connection, "PRAGMA user_version=1;", cancellationToken);
    }

    public static async Task<int> ReadUserVersionAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(databasePath, cancellationToken);
        return Convert.ToInt32(await ScalarAsync(connection, "PRAGMA user_version;", cancellationToken));
    }

    public static async Task<long> ScalarLongAsync(
        string databasePath,
        string sql,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(databasePath, cancellationToken);
        return Convert.ToInt64(await ScalarAsync(connection, sql, cancellationToken));
    }

    public static async Task ExecuteAsync(
        string databasePath,
        string sql,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(databasePath, cancellationToken);
        await ExecuteAsync(connection, sql, cancellationToken);
    }

    private static Task<Microsoft.Data.Sqlite.SqliteConnection> OpenAsync(
        string databasePath,
        CancellationToken cancellationToken) =>
        new SqliteConnectionFactory(databasePath).OpenAsync(cancellationToken);

    private static async Task ExecuteAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<object?> ScalarAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken);
    }
}
