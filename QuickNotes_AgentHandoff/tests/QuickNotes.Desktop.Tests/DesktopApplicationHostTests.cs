using System.Diagnostics;
using QuickNotes.Desktop.Diagnostics;
using QuickNotes.Desktop.Hosting;
using QuickNotes.Desktop.Paths;
using QuickNotes.Desktop.SingleInstance;
using QuickNotes.Desktop.State;
using QuickNotes.Desktop.Startup;
using QuickNotes.Infrastructure.Sqlite;

namespace QuickNotes.Desktop.Tests;

public sealed class DesktopApplicationHostTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 28, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Start_WhenFirstRun_ReturnsReadyAndCreatesState()
    {
        using var temporary = new TestTemporaryDirectory();
        await using var host = CreateHost(temporary.RootPath);

        var result = await host.StartAsync();

        Assert.True(result.CanShowWindow);
        Assert.Equal(SingleInstanceStartResult.Owner, result.InstanceResult);
        Assert.Equal(StartupMode.FirstRunInitialized, result.Assessment!.Mode);
        Assert.True(File.Exists(host.Paths.DatabasePath));
        Assert.True(File.Exists(host.Paths.StatePath));
        Assert.NotNull(host.EditingServices);
        Assert.NotNull(host.BackupServices);
    }

    [Fact]
    public async Task Start_WhenV1Database_MigratesToV2AndKeepsProtectionSnapshot()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(temporary.RootPath);
        await SchemaMigrationTestDatabase.CreateVersionOneAsync(paths.DatabasePath);
        await new ApplicationStateStore(paths.StatePath).WriteAsync(
            CreateReadyState(schemaVersion: 1));
        await using var host = CreateHost(temporary.RootPath);

        var result = await host.StartAsync();

        Assert.True(result.CanShowWindow);
        Assert.Equal(StartupMode.Ready, result.Assessment!.Mode);
        Assert.Equal(2, result.Assessment.DatabaseInfo!.SchemaVersion);
        Assert.Equal(2, await SchemaMigrationTestDatabase.ReadUserVersionAsync(paths.DatabasePath));
        var protectionSnapshot = Assert.Single(
            Directory.GetFiles(paths.MigrationProtectionDirectory, "*.db"));
        Assert.Matches(
            @"^notes-v1-\d{8}-\d{6}-\d{3}\.db$",
            Path.GetFileName(protectionSnapshot));
        var state = await new ApplicationStateStore(paths.StatePath).ReadAsync();
        Assert.Equal(2, state.State!.SchemaVersion);
        Assert.NotNull(host.EditingServices);
        Assert.NotNull(host.BackupServices);
    }

    [Fact]
    public async Task Start_WhenDatabaseMissing_ReturnsNeedsRecovery()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(temporary.RootPath);
        paths.EnsureDirectories();
        await new ApplicationStateStore(paths.StatePath).WriteAsync(CreateReadyState());
        await using var host = CreateHost(temporary.RootPath);

        var result = await host.StartAsync();

        Assert.True(result.CanShowWindow);
        Assert.Equal(StartupMode.NeedsRecovery, result.Assessment!.Mode);
        Assert.Equal(StartupReason.DatabaseMissing, result.Assessment.Reason);
        Assert.False(File.Exists(paths.DatabasePath));
        Assert.Null(host.EditingServices);
        Assert.Null(host.BackupServices);
    }

    [Fact]
    public async Task Start_WhenBlocked_ReturnsWriteBlocked()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(temporary.RootPath);
        await CreateDatabaseAsync(paths);
        await new ApplicationStateStore(paths.StatePath).WriteAsync(
            CreateReadyState().MarkWritesBlocked("restore_rollback_failed", Now));
        await using var host = CreateHost(temporary.RootPath);

        var result = await host.StartAsync();

        Assert.True(result.CanShowWindow);
        Assert.Equal(StartupMode.WriteBlocked, result.Assessment!.Mode);
        Assert.True(host.WriteGate!.IsBlocked);
        Assert.Null(host.EditingServices);
        Assert.Null(host.BackupServices);
    }

    [Fact]
    public async Task Start_AfterFirstRun_ReopensReady()
    {
        using var temporary = new TestTemporaryDirectory();
        var firstHost = CreateHost(temporary.RootPath);
        var first = await firstHost.StartAsync();
        var installationId = first.Assessment!.State!.InstallationId;
        await firstHost.DisposeAsync();

        await using var secondHost = CreateHost(temporary.RootPath);
        var second = await secondHost.StartAsync();

        Assert.True(second.CanShowWindow);
        Assert.Equal(StartupMode.Ready, second.Assessment!.Mode);
        Assert.Equal(installationId, second.Assessment.State!.InstallationId);
        Assert.NotNull(secondHost.EditingServices);
        Assert.NotNull(secondHost.BackupServices);
    }

    [Fact]
    public async Task Start_WithBackupDirectory_ScansProtectionSnapshots()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(temporary.RootPath);
        await CreateDatabaseAsync(paths);
        await new ApplicationStateStore(paths.StatePath).WriteAsync(CreateReadyState());
        var backupDirectory = Path.Combine(temporary.RootPath, "backups");
        Directory.CreateDirectory(backupDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(backupDirectory, "quicknotes-20261028-093000-000-manual.zip"),
            "snapshot");
        var host = new DesktopApplicationHost(
            new DesktopStartupOptions
            {
                DataRoot = temporary.RootPath,
                BackupDirectory = backupDirectory
            },
            new TestTimeProvider(Now));
        await using var disposableHost = host;

        var result = await host.StartAsync();

        Assert.Equal(StartupMode.Ready, result.Assessment!.Mode);
        Assert.Contains(
            result.Assessment.Artifacts.Artifacts,
            artifact => artifact.Kind == StartupArtifactKind.ProtectionSnapshot);
    }

    [Fact]
    public async Task Start_WhenNotOwner_DoesNotOpenDatabase()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(temporary.RootPath);
        var log = new DiagnosticLog(paths.LogDirectory, new TestTimeProvider(Now));
        await using var owner = new SingleInstanceGuard(paths.DataRoot, log);
        Assert.Equal(
            SingleInstanceStartResult.Owner,
            await owner.StartAsync(TimeSpan.FromSeconds(2)));
        await using var host = CreateHost(temporary.RootPath);

        var result = await host.StartAsync();

        Assert.False(result.CanShowWindow);
        Assert.Equal(SingleInstanceStartResult.ActivatedExisting, result.InstanceResult);
        Assert.False(File.Exists(paths.DatabasePath));
        Assert.False(File.Exists(paths.StatePath));
    }

    [Fact]
    public async Task Start_WithDataRootArgument_UsesGivenRoot()
    {
        using var temporary = new TestTemporaryDirectory();
        await using var host = CreateHost(temporary.RootPath);

        await host.StartAsync();

        Assert.Equal(Path.GetFullPath(temporary.RootPath), host.Paths.DataRoot);
    }

    [Fact]
    public async Task SecondProcess_ActivatesOwner()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(temporary.RootPath);
        var log = new DiagnosticLog(paths.LogDirectory, new TestTimeProvider(Now));
        await using var owner = new SingleInstanceGuard(paths.DataRoot, log);
        Assert.Equal(
            SingleInstanceStartResult.Owner,
            await owner.StartAsync(TimeSpan.FromSeconds(2)));
        var activated = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        owner.ActivationRequested += () => activated.TrySetResult();

        var wpfAssembly = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "QuickNotes.Desktop.Wpf", "bin", "Debug", "net10.0-windows",
            "QuickNotes.Desktop.Wpf.dll"));
        Assert.True(
            File.Exists(wpfAssembly),
            "Build QuickNotes.Desktop.Wpf before running the second-process test.");

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(wpfAssembly);
        startInfo.ArgumentList.Add("--single-instance-probe");
        startInfo.ArgumentList.Add("--data-root");
        startInfo.ArgumentList.Add(temporary.RootPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The probe process could not be started.");
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var error = await process.StandardError.ReadToEndAsync();

        Assert.True(process.ExitCode == 0, $"Probe exited with {process.ExitCode}: {error}");
    }

    private static DesktopApplicationHost CreateHost(string dataRoot) =>
        new(
            new DesktopStartupOptions { DataRoot = dataRoot },
            new TestTimeProvider(Now));

    private static ApplicationState CreateReadyState(int schemaVersion = 2) => new()
    {
        FormatVersion = ApplicationState.CurrentFormatVersion,
        InstallationId = Guid.NewGuid(),
        InitializationStatus = ApplicationInitializationStatus.Ready,
        SchemaVersion = schemaVersion,
        LastStartupUtc = Now
    };

    private static async Task CreateDatabaseAsync(DesktopPaths paths)
    {
        paths.EnsureDirectories();
        await new SqliteSchema(
            new SqliteConnectionFactory(paths.DatabasePath)).InitializeAsync();
    }
}
