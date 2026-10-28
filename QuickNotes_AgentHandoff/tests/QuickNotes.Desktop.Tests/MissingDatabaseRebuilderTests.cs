using QuickNotes.Application;
using QuickNotes.Desktop.Diagnostics;
using QuickNotes.Desktop.Paths;
using QuickNotes.Desktop.Recovery;
using QuickNotes.Desktop.State;
using QuickNotes.Domain;
using QuickNotes.Infrastructure.Backup;
using QuickNotes.Infrastructure.Sqlite;

namespace QuickNotes.Desktop.Tests;

public sealed class MissingDatabaseRebuilderTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 28, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Rebuild_MissingDatabase_FromVerifiedPackage_Succeeds()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(Path.Combine(temporary.RootPath, "data"));
        var stateStore = new ApplicationStateStore(paths.StatePath);
        await stateStore.WriteAsync(CreateReadyState());
        var package = await CreatePackageAsync(temporary);
        var rebuilder = CreateRebuilder(paths, stateStore);

        var result = await rebuilder.RebuildAsync(package.PackagePath, quarantineCorruptCurrent: false);

        Assert.Equal(MissingDatabaseRebuildStatus.Succeeded, result.Status);
        Assert.True(File.Exists(paths.DatabasePath));
        Assert.Equal(1, result.RestoredCounts!.ActiveNotes);
        Assert.False(result.ProtectionSnapshotCreated);
        Assert.Empty(Directory.GetDirectories(paths.DataRoot, ".quicknotes-recovery-*"));
        var state = await stateStore.ReadAsync();
        Assert.Equal(package.Manifest.DatabaseSha256, state.State!.RecoverySourceSha256);
        Assert.False(state.State.RecoveryProtectionSnapshotCreated);
    }

    [Fact]
    public async Task Rebuild_CorruptPackage_LeavesDatabaseMissingAndPreservesStaging()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(Path.Combine(temporary.RootPath, "data"));
        var stateStore = new ApplicationStateStore(paths.StatePath);
        await stateStore.WriteAsync(CreateReadyState());
        var corruptPackage = Path.Combine(temporary.RootPath, "corrupt.zip");
        await File.WriteAllTextAsync(corruptPackage, "not a zip");
        var rebuilder = CreateRebuilder(paths, stateStore);

        var result = await rebuilder.RebuildAsync(corruptPackage, quarantineCorruptCurrent: false);

        Assert.Equal(MissingDatabaseRebuildStatus.Failed, result.Status);
        Assert.False(File.Exists(paths.DatabasePath));
        Assert.NotNull(result.StagingDirectory);
        Assert.True(Directory.Exists(result.StagingDirectory));
    }

    [Fact]
    public async Task Rebuild_WithValidCurrentDatabase_Refuses()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(Path.Combine(temporary.RootPath, "data"));
        await CreateDatabaseAsync(paths);
        var stateStore = new ApplicationStateStore(paths.StatePath);
        await stateStore.WriteAsync(CreateReadyState());
        var package = await CreatePackageAsync(temporary);
        var rebuilder = CreateRebuilder(paths, stateStore);
        var before = await new SqliteRestoreStore().TryGetDatabaseHashAsync(paths.DatabasePath);

        var result = await rebuilder.RebuildAsync(package.PackagePath, quarantineCorruptCurrent: false);

        Assert.Equal(MissingDatabaseRebuildStatus.Failed, result.Status);
        Assert.Equal("current_database_present", result.ErrorCode);
        Assert.Equal(before, await new SqliteRestoreStore().TryGetDatabaseHashAsync(paths.DatabasePath));
    }

    [Fact]
    public async Task Rebuild_CorruptCurrent_RequiresQuarantineConfirmation()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(Path.Combine(temporary.RootPath, "data"));
        paths.EnsureDirectories();
        await File.WriteAllTextAsync(paths.DatabasePath, "not a database");
        var stateStore = new ApplicationStateStore(paths.StatePath);
        await stateStore.WriteAsync(CreateReadyState());
        var package = await CreatePackageAsync(temporary);
        var rebuilder = CreateRebuilder(paths, stateStore);

        var result = await rebuilder.RebuildAsync(package.PackagePath, quarantineCorruptCurrent: false);

        Assert.Equal(MissingDatabaseRebuildStatus.Failed, result.Status);
        Assert.Equal("current_database_corrupt", result.ErrorCode);
        Assert.Equal("not a database", await File.ReadAllTextAsync(paths.DatabasePath));
    }

    [Fact]
    public async Task Rebuild_CorruptCurrent_WithConfirmation_QuarantinesAndPublishes()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(Path.Combine(temporary.RootPath, "data"));
        paths.EnsureDirectories();
        await File.WriteAllTextAsync(paths.DatabasePath, "not a database");
        var stateStore = new ApplicationStateStore(paths.StatePath);
        await stateStore.WriteAsync(CreateReadyState());
        var package = await CreatePackageAsync(temporary);
        var rebuilder = CreateRebuilder(paths, stateStore);

        var result = await rebuilder.RebuildAsync(package.PackagePath, quarantineCorruptCurrent: true);

        Assert.Equal(MissingDatabaseRebuildStatus.Succeeded, result.Status);
        Assert.True(File.Exists(paths.DatabasePath));
        Assert.NotNull(result.QuarantinePath);
        Assert.True(File.Exists(result.QuarantinePath));
        Assert.Equal("not a database", await File.ReadAllTextAsync(result.QuarantinePath));
        Assert.Equal(1, result.RestoredCounts!.ActiveNotes);
        Assert.False(result.ProtectionSnapshotCreated);
    }

    [Fact]
    public async Task Rebuild_BlockedWrites_WithVerifiedBackup_ClearsBlockAfterVerification()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(Path.Combine(temporary.RootPath, "data"));
        await CreateDatabaseAsync(paths);
        var stateStore = new ApplicationStateStore(paths.StatePath);
        await stateStore.WriteAsync(
            CreateReadyState().MarkWritesBlocked("restore_rollback_failed", Now));
        var package = await CreatePackageAsync(temporary);
        var rebuilder = CreateRebuilder(paths, stateStore);

        var result = await rebuilder.RebuildAsync(
            package.PackagePath,
            quarantineCorruptCurrent: true,
            recoverBlockedWrites: true);

        Assert.Equal(MissingDatabaseRebuildStatus.Succeeded, result.Status);
        Assert.NotNull(result.QuarantinePath);
        Assert.True(File.Exists(result.QuarantinePath));
        Assert.Equal(1, result.RestoredCounts!.ActiveNotes);
        var state = await stateStore.ReadAsync();
        Assert.False(state.State!.WritesBlocked);
        Assert.Null(state.State.WriteBlockReason);
        Assert.Equal(package.Manifest.DatabaseSha256, state.State.RecoverySourceSha256);
    }

    [Fact]
    public async Task Rebuild_PostPublishVerificationFailure_DoesNotReportSuccess()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(Path.Combine(temporary.RootPath, "data"));
        var stateStore = new ApplicationStateStore(paths.StatePath);
        await stateStore.WriteAsync(CreateReadyState());
        var package = await CreatePackageAsync(temporary);
        var rebuilder = new MissingDatabaseRebuilder(
            paths,
            new BackupPackageVerifier(),
            new BadHashRestoreStore(new SqliteRestoreStore(), paths.DatabasePath),
            stateStore,
            new DiagnosticLog(paths.LogDirectory, new TestTimeProvider(Now)),
            new TestTimeProvider(Now));

        var result = await rebuilder.RebuildAsync(package.PackagePath, quarantineCorruptCurrent: false);

        Assert.Equal(MissingDatabaseRebuildStatus.Failed, result.Status);
        Assert.False(File.Exists(paths.DatabasePath));
        Assert.NotNull(result.StagingDirectory);
        Assert.True(File.Exists(Path.Combine(result.StagingDirectory!, "failed-notes.db")));
        Assert.Null((await stateStore.ReadAsync()).State!.RecoverySourceSha256);
    }

    private static MissingDatabaseRebuilder CreateRebuilder(
        DesktopPaths paths,
        ApplicationStateStore stateStore) =>
        new(
            paths,
            new BackupPackageVerifier(),
            new SqliteRestoreStore(),
            stateStore,
            new DiagnosticLog(paths.LogDirectory, new TestTimeProvider(Now)),
            new TestTimeProvider(Now));

    private static ApplicationState CreateReadyState() => new()
    {
        FormatVersion = ApplicationState.CurrentFormatVersion,
        InstallationId = Guid.NewGuid(),
        InitializationStatus = ApplicationInitializationStatus.Ready,
        SchemaVersion = 1,
        LastStartupUtc = Now
    };

    private static async Task<(string PackagePath, BackupManifest Manifest)> CreatePackageAsync(
        TestTemporaryDirectory temporary)
    {
        var sourceDirectory = Path.Combine(temporary.RootPath, "source");
        Directory.CreateDirectory(sourceDirectory);
        var sourceDatabasePath = Path.Combine(sourceDirectory, "notes.db");
        var factory = new SqliteConnectionFactory(sourceDatabasePath);
        await new SqliteSchema(factory).InitializeAsync();
        var repository = new SqliteNoteRepository(factory);
        await repository.CreateAsync(
            new Note
            {
                Id = Guid.NewGuid(),
                Title = "backup note",
                Body = "backup body",
                CreatedAtUtc = Now,
                ModifiedAtUtc = Now,
                CategoryId = Category.UncategorizedId
            },
            Array.Empty<Guid>());
        var snapshotPath = Path.Combine(temporary.RootPath, "snapshot.db");
        await new SqliteSnapshotProvider(sourceDatabasePath).CreateAsync(snapshotPath);
        var backupDirectory = Path.Combine(temporary.RootPath, "backups");
        Directory.CreateDirectory(backupDirectory);
        var package = await new ZipBackupPackageStore().PublishAsync(
            snapshotPath,
            1,
            new BackupRequest
            {
                BackupDirectory = backupDirectory,
                Type = BackupType.Manual
            },
            Now,
            Now.ToOffset(TimeSpan.FromHours(8)));
        return (package.PackagePath, package.Manifest);
    }

    private static async Task CreateDatabaseAsync(DesktopPaths paths)
    {
        paths.EnsureDirectories();
        await new SqliteSchema(
            new SqliteConnectionFactory(paths.DatabasePath)).InitializeAsync();
    }

    private sealed class BadHashRestoreStore : IRestoreStore
    {
        private readonly IRestoreStore _inner;
        private readonly string _currentDatabasePath;

        public BadHashRestoreStore(IRestoreStore inner, string currentDatabasePath)
        {
            _inner = inner;
            _currentDatabasePath = currentDatabasePath;
        }

        public Task<RestoreDatabaseInfo> InspectAsync(
            string databasePath,
            CancellationToken cancellationToken = default) =>
            _inner.InspectAsync(databasePath, cancellationToken);

        public Task<string?> TryGetDatabaseHashAsync(
            string databasePath,
            CancellationToken cancellationToken = default) =>
            string.Equals(databasePath, _currentDatabasePath, StringComparison.OrdinalIgnoreCase)
                ? Task.FromResult<string?>("bad")
                : _inner.TryGetDatabaseHashAsync(databasePath, cancellationToken);

        public Task<RestoreDatabaseInfo> PrepareCurrentAsync(
            string currentDatabasePath,
            CancellationToken cancellationToken = default) =>
            _inner.PrepareCurrentAsync(currentDatabasePath, cancellationToken);

        public Task SwitchAsync(
            string stagedDatabasePath,
            string currentDatabasePath,
            string rollbackDatabasePath,
            CancellationToken cancellationToken = default) =>
            _inner.SwitchAsync(
                stagedDatabasePath,
                currentDatabasePath,
                rollbackDatabasePath,
                cancellationToken);

        public Task RollbackAsync(
            string rollbackDatabasePath,
            string currentDatabasePath,
            string failedDatabasePath,
            CancellationToken cancellationToken = default) =>
            _inner.RollbackAsync(
                rollbackDatabasePath,
                currentDatabasePath,
                failedDatabasePath,
                cancellationToken);
    }
}
