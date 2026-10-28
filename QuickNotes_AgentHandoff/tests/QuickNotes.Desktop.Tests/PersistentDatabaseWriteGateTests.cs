using QuickNotes.Application;
using QuickNotes.Desktop.Diagnostics;
using QuickNotes.Desktop.Paths;
using QuickNotes.Desktop.Recovery;
using QuickNotes.Desktop.State;
using QuickNotes.Infrastructure.Backup;
using QuickNotes.Infrastructure.Sqlite;

namespace QuickNotes.Desktop.Tests;

public sealed class PersistentDatabaseWriteGateTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 28, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task BlockWrites_PersistsStateAcrossNewGateInstance()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(temporary.RootPath);
        paths.EnsureDirectories();
        var stateStore = new ApplicationStateStore(paths.StatePath);
        await stateStore.WriteAsync(CreateReadyState());
        var log = new DiagnosticLog(paths.LogDirectory, new TestTimeProvider(Now));
        var gate = new PersistentDatabaseWriteGate(stateStore, log, timeProvider: new TestTimeProvider(Now));

        gate.BlockWrites();

        Assert.True(gate.IsBlocked);
        var persisted = await new ApplicationStateStore(paths.StatePath).ReadAsync();
        Assert.True(persisted.State!.WritesBlocked);
        await Assert.ThrowsAsync<DatabaseWriteBlockedException>(
            () => new PersistentDatabaseWriteGate(
                    new ApplicationStateStore(paths.StatePath),
                    log,
                    initiallyBlocked: true)
                .EnterWriteAsync());
    }

    [Fact]
    public async Task BlockWrites_WhenStateMissing_LogsAndStillBlocksInMemory()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(temporary.RootPath);
        var stateStore = new ApplicationStateStore(paths.StatePath);
        var log = new DiagnosticLog(paths.LogDirectory, new TestTimeProvider(Now));
        var gate = new PersistentDatabaseWriteGate(stateStore, log, timeProvider: new TestTimeProvider(Now));

        gate.BlockWrites();

        Assert.True(gate.IsBlocked);
        await Assert.ThrowsAsync<DatabaseWriteBlockedException>(() => gate.EnterWriteAsync());
        var logPath = Path.Combine(paths.LogDirectory, "quicknotes-20261028.log");
        Assert.True(File.Exists(logPath));
        Assert.Contains("write block could not be persisted", await File.ReadAllTextAsync(logPath));
    }

    [Fact]
    public async Task EnterWrite_WhenNotBlocked_AcquiresLease()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(temporary.RootPath);
        var stateStore = new ApplicationStateStore(paths.StatePath);
        var log = new DiagnosticLog(paths.LogDirectory, new TestTimeProvider(Now));
        var gate = new PersistentDatabaseWriteGate(stateStore, log, timeProvider: new TestTimeProvider(Now));

        await using var lease = await gate.EnterWriteAsync();

        Assert.NotNull(lease);
        Assert.False(gate.IsBlocked);
    }

    private static ApplicationState CreateReadyState() => new()
    {
        FormatVersion = ApplicationState.CurrentFormatVersion,
        InstallationId = Guid.NewGuid(),
        InitializationStatus = ApplicationInitializationStatus.Ready,
        SchemaVersion = 2,
        LastStartupUtc = Now
    };
}
