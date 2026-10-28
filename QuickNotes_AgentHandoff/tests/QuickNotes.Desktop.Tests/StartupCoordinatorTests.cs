using QuickNotes.Application;
using QuickNotes.Desktop.Diagnostics;
using QuickNotes.Desktop.Paths;
using QuickNotes.Desktop.Recovery;
using QuickNotes.Desktop.Startup;
using QuickNotes.Desktop.State;
using QuickNotes.Infrastructure.Sqlite;

namespace QuickNotes.Desktop.Tests;

public sealed class StartupCoordinatorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 28, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task FirstRun_NoStateNoDatabase_CreatesStateAndDatabase()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(temporary.RootPath);
        var stateStore = CreateStateStore(paths);
        var coordinator = CreateCoordinator(paths, stateStore, CreateJournalStore(paths));

        var result = await coordinator.StartAsync();

        Assert.Equal(StartupMode.FirstRunInitialized, result.Mode);
        Assert.Equal(StartupReason.Initialized, result.Reason);
        Assert.True(result.CanEnterNormalUse);
        Assert.True(File.Exists(paths.DatabasePath));
        var state = await stateStore.ReadAsync();
        Assert.Equal(ApplicationStateReadStatus.Found, state.Status);
        Assert.Equal(ApplicationInitializationStatus.Ready, state.State!.InitializationStatus);
        Assert.Equal(2, state.State.SchemaVersion);
    }

    [Fact]
    public async Task FirstRun_InitializationFailure_DoesNotWriteReadyState()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(temporary.RootPath);
        var stateStore = CreateStateStore(paths);
        var coordinator = CreateCoordinator(
            paths,
            stateStore,
            CreateJournalStore(paths),
            _ => throw new InvalidOperationException("simulated initialization failure"));

        var result = await coordinator.StartAsync();

        Assert.Equal(StartupMode.ReadOnlyDiagnostic, result.Mode);
        Assert.Equal(StartupReason.InitializationIncomplete, result.Reason);
        var state = await stateStore.ReadAsync();
        Assert.Equal(ApplicationStateReadStatus.Found, state.Status);
        Assert.Equal(ApplicationInitializationStatus.Initializing, state.State!.InitializationStatus);
        Assert.False(File.Exists(paths.DatabasePath));
    }

    [Fact]
    public async Task RetryInitialization_AfterFailure_CanComplete()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(temporary.RootPath);
        var stateStore = CreateStateStore(paths);
        var failing = CreateCoordinator(
            paths,
            stateStore,
            CreateJournalStore(paths),
            _ => throw new InvalidOperationException("simulated initialization failure"));
        await failing.StartAsync();

        var retrying = CreateCoordinator(paths, stateStore, CreateJournalStore(paths));
        var result = await retrying.RetryInitializationAsync();

        Assert.Equal(StartupMode.FirstRunInitialized, result.Mode);
        Assert.True(File.Exists(paths.DatabasePath));
        var state = await stateStore.ReadAsync();
        Assert.Equal(ApplicationInitializationStatus.Ready, state.State!.InitializationStatus);
    }

    [Fact]
    public async Task Ready_WithStateAndValidDatabase()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(temporary.RootPath);
        var stateStore = CreateStateStore(paths);
        await CreateDatabaseAsync(paths);
        await stateStore.WriteAsync(CreateReadyState());

        var result = await CreateCoordinator(paths, stateStore, CreateJournalStore(paths)).StartAsync();

        Assert.Equal(StartupMode.Ready, result.Mode);
        Assert.Equal(2, result.DatabaseInfo!.SchemaVersion);
        Assert.True(result.CanEnterNormalUse);
    }

    [Fact]
    public async Task StateReadyWithoutDatabase_NeedsRecovery_DoesNotCreateDatabase()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(temporary.RootPath);
        var stateStore = CreateStateStore(paths);
        paths.EnsureDirectories();
        await stateStore.WriteAsync(CreateReadyState());

        var result = await CreateCoordinator(paths, stateStore, CreateJournalStore(paths)).StartAsync();

        Assert.Equal(StartupMode.NeedsRecovery, result.Mode);
        Assert.Equal(StartupReason.DatabaseMissing, result.Reason);
        Assert.False(File.Exists(paths.DatabasePath));
    }

    [Fact]
    public async Task DatabaseWithoutState_ReadOnlyDiagnostic_DoesNotDeleteOrInit()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(temporary.RootPath);
        var stateStore = CreateStateStore(paths);
        await CreateDatabaseAsync(paths);

        var result = await CreateCoordinator(paths, stateStore, CreateJournalStore(paths)).StartAsync();

        Assert.Equal(StartupMode.ReadOnlyDiagnostic, result.Mode);
        Assert.Equal(StartupReason.StateMissingWithDatabase, result.Reason);
        Assert.True(File.Exists(paths.DatabasePath));
    }

    [Fact]
    public async Task StateInvalid_ReadOnlyDiagnostic_PreservesState()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(temporary.RootPath);
        var stateStore = CreateStateStore(paths);
        paths.EnsureDirectories();
        await File.WriteAllTextAsync(paths.StatePath, "{");

        var result = await CreateCoordinator(paths, stateStore, CreateJournalStore(paths)).StartAsync();

        Assert.Equal(StartupMode.ReadOnlyDiagnostic, result.Mode);
        Assert.Equal(StartupReason.StateInvalid, result.Reason);
        Assert.True(File.Exists(paths.StatePath));
    }

    [Fact]
    public async Task InitializationIncomplete_ReadOnlyDiagnostic()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(temporary.RootPath);
        var stateStore = CreateStateStore(paths);
        paths.EnsureDirectories();
        await stateStore.WriteAsync(ApplicationState.CreateInitializing(Guid.NewGuid(), Now));

        var result = await CreateCoordinator(paths, stateStore, CreateJournalStore(paths)).StartAsync();

        Assert.Equal(StartupMode.ReadOnlyDiagnostic, result.Mode);
        Assert.Equal(StartupReason.InitializationIncomplete, result.Reason);
        Assert.False(File.Exists(paths.DatabasePath));
    }

    [Fact]
    public async Task BlockedState_CrossesNewCoordinator_StaysBlocked()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(temporary.RootPath);
        var stateStore = CreateStateStore(paths);
        await CreateDatabaseAsync(paths);
        await stateStore.WriteAsync(
            CreateReadyState().MarkWritesBlocked("restore_rollback_failed", Now));

        var result = await CreateCoordinator(
                paths,
                CreateStateStore(paths),
                CreateJournalStore(paths))
            .StartAsync();

        Assert.Equal(StartupMode.WriteBlocked, result.Mode);
        Assert.Equal(StartupReason.WritesBlocked, result.Reason);
        Assert.Equal("database_writes_blocked", result.ErrorCode);
        Assert.True(result.State!.WritesBlocked);
    }

    [Fact]
    public async Task WalSidecar_NeedsRecovery_DoesNotDeleteSidecar()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(temporary.RootPath);
        var stateStore = CreateStateStore(paths);
        await CreateDatabaseAsync(paths);
        await stateStore.WriteAsync(CreateReadyState());
        await File.WriteAllTextAsync(paths.DatabasePath + "-wal", "wal");

        var result = await CreateCoordinator(paths, stateStore, CreateJournalStore(paths)).StartAsync();

        Assert.Equal(StartupMode.NeedsRecovery, result.Mode);
        Assert.Equal(StartupReason.WalSidecarPresent, result.Reason);
        Assert.True(File.Exists(paths.DatabasePath + "-wal"));
    }

    [Fact]
    public async Task CorruptDatabase_NeedsRecovery_PreservesFile()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(temporary.RootPath);
        var stateStore = CreateStateStore(paths);
        paths.EnsureDirectories();
        await File.WriteAllTextAsync(paths.DatabasePath, "not a database");
        await stateStore.WriteAsync(CreateReadyState());

        var result = await CreateCoordinator(paths, stateStore, CreateJournalStore(paths)).StartAsync();

        Assert.Equal(StartupMode.NeedsRecovery, result.Mode);
        Assert.Equal(StartupReason.DatabaseCorrupt, result.Reason);
        Assert.True(File.Exists(paths.DatabasePath));
    }

    [Fact]
    public async Task RestoreArtifacts_AreRetained()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(temporary.RootPath);
        var stateStore = CreateStateStore(paths);
        await CreateDatabaseAsync(paths);
        await stateStore.WriteAsync(CreateReadyState());
        var restoreDirectory = Path.Combine(paths.DataRoot, ".quicknotes-restore-abc");
        Directory.CreateDirectory(restoreDirectory);
        await File.WriteAllTextAsync(Path.Combine(restoreDirectory, "rollback.db"), "rollback");

        var result = await CreateCoordinator(paths, stateStore, CreateJournalStore(paths)).StartAsync();

        Assert.Equal(StartupMode.Ready, result.Mode);
        Assert.Contains(result.Artifacts.Artifacts, artifact => artifact.Kind == StartupArtifactKind.RestoreDirectory);
        Assert.True(Directory.Exists(restoreDirectory));
        Assert.True(File.Exists(Path.Combine(restoreDirectory, "rollback.db")));
    }

    [Fact]
    public async Task IncompleteRestoreJournal_NeedsRecovery_DoesNotCleanArtifacts()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(temporary.RootPath);
        var stateStore = CreateStateStore(paths);
        var journalStore = CreateJournalStore(paths);
        await CreateDatabaseAsync(paths);
        await stateStore.WriteAsync(CreateReadyState());
        var staging = Path.Combine(paths.DataRoot, ".quicknotes-restore-abc");
        Directory.CreateDirectory(staging);
        await journalStore.WriteAsync(CreateJournal(RestoreStage.Switching, staging));

        var result = await CreateCoordinator(paths, stateStore, journalStore).StartAsync();

        Assert.Equal(StartupMode.NeedsRecovery, result.Mode);
        Assert.Equal(StartupReason.RestoreJournalIncomplete, result.Reason);
        Assert.True(File.Exists(journalStore.JournalPath));
        Assert.True(Directory.Exists(staging));
    }

    [Fact]
    public async Task RestoreJournalCorrupt_ReadOnlyDiagnostic_PreservesJournal()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(temporary.RootPath);
        var stateStore = CreateStateStore(paths);
        var journalStore = CreateJournalStore(paths);
        paths.EnsureDirectories();
        await File.WriteAllTextAsync(journalStore.JournalPath, "{");

        var result = await CreateCoordinator(paths, stateStore, journalStore).StartAsync();

        Assert.Equal(StartupMode.ReadOnlyDiagnostic, result.Mode);
        Assert.Equal(StartupReason.RestoreJournalInvalid, result.Reason);
        Assert.True(File.Exists(journalStore.JournalPath));
    }

    [Fact]
    public async Task RestoreJournalCompleted_DoesNotBlock()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(temporary.RootPath);
        var stateStore = CreateStateStore(paths);
        var journalStore = CreateJournalStore(paths);
        await CreateDatabaseAsync(paths);
        await stateStore.WriteAsync(CreateReadyState());
        await journalStore.WriteAsync(CreateJournal(RestoreStage.Completed, paths.DataRoot));

        var result = await CreateCoordinator(paths, stateStore, journalStore).StartAsync();

        Assert.Equal(StartupMode.Ready, result.Mode);
    }

    [Fact]
    public async Task StateMissingWithDrafts_ReadOnlyDiagnostic_DoesNotInitialize()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(temporary.RootPath);
        paths.EnsureDirectories();
        await File.WriteAllTextAsync(Path.Combine(paths.DraftDirectory, "draft.json"), "{}");

        var result = await CreateCoordinator(
                paths,
                CreateStateStore(paths),
                CreateJournalStore(paths))
            .StartAsync();

        Assert.Equal(StartupMode.ReadOnlyDiagnostic, result.Mode);
        Assert.Equal(StartupReason.UnverifiedArtifacts, result.Reason);
        Assert.False(File.Exists(paths.DatabasePath));
    }

    private static StartupCoordinator CreateCoordinator(
        DesktopPaths paths,
        ApplicationStateStore stateStore,
        IRestoreJournalStore journalStore,
        Func<CancellationToken, Task>? initializeSchema = null)
    {
        var time = new TestTimeProvider(Now);
        return new StartupCoordinator(
            paths,
            stateStore,
            journalStore,
            new StartupArtifactScanner(paths),
            new SqliteRestoreStore(),
            initializeSchema ?? (ct => new SqliteSchema(
                new SqliteConnectionFactory(paths.DatabasePath)).InitializeAsync(ct)),
            new DiagnosticLog(paths.LogDirectory, time),
            time);
    }

    private static ApplicationStateStore CreateStateStore(DesktopPaths paths) =>
        new(paths.StatePath);

    private static FileRestoreJournalStore CreateJournalStore(DesktopPaths paths) =>
        new(paths.RestoreJournalPath);

    private static ApplicationState CreateReadyState() => new()
    {
        FormatVersion = ApplicationState.CurrentFormatVersion,
        InstallationId = Guid.NewGuid(),
        InitializationStatus = ApplicationInitializationStatus.Ready,
        SchemaVersion = 2,
        LastStartupUtc = Now
    };

    private static async Task CreateDatabaseAsync(DesktopPaths paths)
    {
        paths.EnsureDirectories();
        await new SqliteSchema(
            new SqliteConnectionFactory(paths.DatabasePath)).InitializeAsync();
    }

    private static RestoreJournal CreateJournal(RestoreStage stage, string stagingDirectory) => new()
    {
        FormatVersion = RestoreJournal.CurrentFormatVersion,
        RestoreId = Guid.NewGuid(),
        Stage = stage,
        CurrentDatabasePath = "notes.db",
        StagingDirectory = stagingDirectory,
        StartedAtUtc = Now,
        UpdatedAtUtc = Now.AddMinutes(1)
    };
}
