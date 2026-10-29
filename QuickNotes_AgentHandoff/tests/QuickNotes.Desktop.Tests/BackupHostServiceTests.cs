using QuickNotes.Application;
using QuickNotes.Desktop.Backup;
using QuickNotes.Desktop.Diagnostics;
using QuickNotes.Desktop.Paths;
using QuickNotes.Desktop.Settings;
using QuickNotes.Domain;
using QuickNotes.Infrastructure.Backup;

namespace QuickNotes.Desktop.Tests;

public sealed class BackupHostServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 28, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Start_ImmediatelyChecks_WhenDirectoryConfigured()
    {
        using var context = await HostContext.CreateAsync(configured: true);
        var daily = context.Daily;

        await context.Host.StartAsync();
        await WaitUntilAsync(() => daily.Calls == 1);
        await context.Host.StopAsync();

        Assert.Equal(1, daily.Calls);
    }

    [Fact]
    public async Task NoConfiguredDirectory_StatusIsNotConfigured_AndNoBackup()
    {
        using var context = await HostContext.CreateAsync(configured: false);

        var result = await context.Host.RunScheduledCheckAsync();
        var status = await context.Host.GetStatusAsync();

        Assert.Equal(BackupHostCheckStatus.NotConfigured, result.Status);
        Assert.Equal(0, context.Daily.Calls);
        Assert.False(status.AutoBackupConfigured);
    }

    [Fact]
    public async Task ScheduledAndManualChecks_AreNotReentrant()
    {
        using var context = await HostContext.CreateAsync(configured: true);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Daily.Gate = gate.Task;

        var first = context.Host.RunScheduledCheckAsync();
        await context.Daily.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = await context.Host.RunScheduledCheckAsync();
        var manual = await context.Host.CreateManualBackupAsync();
        gate.SetResult();
        await first;

        Assert.Equal(BackupHostCheckStatus.Busy, second.Status);
        Assert.Equal(BackupOperationStatus.Busy, manual.Status);
    }

    [Fact]
    public async Task SetDirectory_PersistsAndEnablesStatus()
    {
        using var context = await HostContext.CreateAsync(configured: false);

        var result = await context.Host.SetBackupDirectoryAsync(context.BackupDirectory);
        var status = await context.Host.GetStatusAsync();
        var settings = await context.DeviceSettings.ReadAsync();

        Assert.Equal(BackupOperationStatus.Succeeded, result.Status);
        Assert.True(status.AutoBackupConfigured);
        Assert.Equal(context.BackupDirectory, settings.Settings!.BackupDirectory);
    }

    [Fact]
    public async Task SetDirectory_RejectsDataRoot()
    {
        using var context = await HostContext.CreateAsync(configured: false);

        var result = await context.Host.SetBackupDirectoryAsync(context.Paths.DataRoot);

        Assert.Equal(BackupOperationStatus.InvalidDirectory, result.Status);
        Assert.False((await context.Host.GetStatusAsync()).AutoBackupConfigured);
    }

    [Fact]
    public async Task ManualBackup_UpdatesRuntimeStatus()
    {
        using var context = await HostContext.CreateAsync(configured: true);

        var result = await context.Host.CreateManualBackupAsync();
        var status = await context.Host.GetStatusAsync();

        Assert.Equal(BackupOperationStatus.Succeeded, result.Status);
        Assert.Equal(1, context.Backups.Calls);
        Assert.Equal(Now, status.LastSuccessAtUtc);
        Assert.NotNull(status.LastSuccessfulPackagePath);
    }

    [Fact]
    public async Task SuspendForRestore_BlocksManualAndScheduledOperations()
    {
        using var context = await HostContext.CreateAsync(configured: true);

        await using (await context.Host.SuspendForRestoreAsync())
        {
            Assert.Equal(
                BackupOperationStatus.Busy,
                (await context.Host.CreateManualBackupAsync()).Status);
            Assert.Equal(
                BackupHostCheckStatus.Busy,
                (await context.Host.RunScheduledCheckAsync()).Status);
        }

        Assert.Equal(
            BackupOperationStatus.Succeeded,
            (await context.Host.CreateManualBackupAsync()).Status);
    }

    [Fact]
    public async Task TriggerWakeCheck_UsesConfiguredDirectory()
    {
        using var context = await HostContext.CreateAsync(configured: true);

        await context.Host.TriggerWakeCheckAsync();

        Assert.Equal(1, context.Daily.Calls);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Condition was not reached in time.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class HostContext : IDisposable, IAsyncDisposable
    {
        private HostContext(
            TestTemporaryDirectory temporary,
            DesktopPaths paths,
            string backupDirectory,
            FileDeviceSettingsStore deviceSettings,
            FakeDailyBackupService daily,
            FakeBackupCoordinator backups,
            BackupHostService host)
        {
            Temporary = temporary;
            Paths = paths;
            BackupDirectory = backupDirectory;
            DeviceSettings = deviceSettings;
            Daily = daily;
            Backups = backups;
            Host = host;
        }

        public TestTemporaryDirectory Temporary { get; }
        public DesktopPaths Paths { get; }
        public string BackupDirectory { get; }
        public FileDeviceSettingsStore DeviceSettings { get; }
        public FakeDailyBackupService Daily { get; }
        public FakeBackupCoordinator Backups { get; }
        public BackupHostService Host { get; }

        public static async Task<HostContext> CreateAsync(bool configured)
        {
            var temporary = new TestTemporaryDirectory();
            var paths = DesktopPaths.Create(Path.Combine(temporary.RootPath, "data"));
            paths.EnsureDirectories();
            var backupDirectory = Path.Combine(temporary.RootPath, "backups");
            Directory.CreateDirectory(backupDirectory);
            var deviceSettings = new FileDeviceSettingsStore(paths.DeviceSettingsPath);
            if (configured)
            {
                await deviceSettings.WriteAsync(new DeviceSettings
                {
                    BackupDirectory = backupDirectory
                });
            }

            var daily = new FakeDailyBackupService();
            var backups = new FakeBackupCoordinator();
            var host = new BackupHostService(
                paths,
                deviceSettings,
                new FileBackupRuntimeStatusStore(paths.BackupRuntimeStatusPath),
                daily,
                backups,
                new FileBackupCatalog(),
                new BackupPackageVerifier(),
                timeProvider: new TestTimeProvider(Now),
                log: new DiagnosticLog(paths.LogDirectory, new TestTimeProvider(Now)));
            return new HostContext(
                temporary,
                paths,
                backupDirectory,
                deviceSettings,
                daily,
                backups,
                host);
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            Temporary.Dispose();
        }

        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed class FakeDailyBackupService : IDailyBackupService
    {
        public int Calls { get; private set; }
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? Gate { get; set; }

        public async Task<DailyBackupResult> RunCheckAsync(
            string backupDirectory,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Entered.TrySetResult();
            if (Gate is not null)
            {
                await Gate.WaitAsync(cancellationToken);
            }

            return new DailyBackupResult
            {
                Outcome = DailyBackupOutcome.NoChanges,
                CheckedAtUtc = Now,
                LocalDate = DateOnly.FromDateTime(Now.UtcDateTime)
            };
        }

        public Task RunPeriodicAsync(
            string backupDirectory,
            TimeSpan interval,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeBackupCoordinator : IBackupCoordinator
    {
        public int Calls { get; private set; }

        public Task<BackupResult> CreateAsync(
            BackupRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            var manifest = new BackupManifest
            {
                FormatVersion = BackupManifest.CurrentFormatVersion,
                BackupType = request.Type,
                CreatedAtUtc = Now,
                CreatedAtLocal = Now.ToOffset(TimeSpan.FromHours(8)),
                SchemaVersion = 2,
                DatabaseFileName = BackupManifest.DatabaseEntryName,
                DatabaseSizeBytes = 1024,
                DatabaseSha256 = new string('a', 64)
            };
            return Task.FromResult(new BackupResult
            {
                Status = BackupStatus.Succeeded,
                PackagePath = Path.Combine(request.BackupDirectory, "manual.zip"),
                Manifest = manifest
            });
        }
    }
}
