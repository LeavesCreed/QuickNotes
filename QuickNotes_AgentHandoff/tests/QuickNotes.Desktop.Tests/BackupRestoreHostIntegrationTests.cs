using QuickNotes.Application;
using QuickNotes.Desktop.Hosting;
using QuickNotes.Desktop.Paths;
using QuickNotes.Desktop.Startup;
using QuickNotes.Domain;

namespace QuickNotes.Desktop.Tests;

public sealed class BackupRestoreHostIntegrationTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 28, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task ManualBackup_ListVerifyDelete_AndRestoreRefreshesServices()
    {
        using var temporary = new TestTemporaryDirectory();
        var backupDirectory = Path.Combine(temporary.RootPath, "backups");
        Directory.CreateDirectory(backupDirectory);
        await using var host = CreateHost(Path.Combine(temporary.RootPath, "data"));
        await host.StartAsync();
        var hostBackup = host.BackupServices!.Host;
        Assert.Equal(
            BackupOperationStatus.Succeeded,
            (await hostBackup.SetBackupDirectoryAsync(backupDirectory)).Status);

        var session = await host.EditingServices!.Workspace.CreateNewAsync();
        session.ChangeText("original title", "original body");
        Assert.Equal(SaveStatus.Saved, (await session.SaveNowAsync()).Status);
        var noteId = session.NoteId;
        var manual = await hostBackup.CreateManualBackupAsync();
        Assert.Equal(BackupOperationStatus.Succeeded, manual.Status);
        var packagePath = manual.PackagePath!;
        Assert.True(File.Exists(packagePath));

        var catalog = await hostBackup.ListBackupsAsync();
        Assert.Single(catalog.ValidEntries);
        var verification = await hostBackup.VerifyBackupAsync(packagePath);
        Assert.Equal(BackupVerificationStatus.Valid, verification.Status);

        session.ChangeText("changed title", "changed body");
        Assert.Equal(SaveStatus.Saved, (await session.SaveNowAsync()).Status);
        await session.FlushDraftAsync();
        session.Invalidate();
        var restoreApplied = 0;
        host.RestoreApplied += () => restoreApplied++;
        var request = new RestoreRequest
        {
            PackagePath = packagePath,
            CurrentDatabasePath = host.Paths.DatabasePath,
            BackupDirectory = backupDirectory,
            AllowPendingDrafts = true
        };
        var preflight = await host.BackupServices.Restore.PreflightAsync(request);
        Assert.True(preflight.CanProceed);
        Assert.False(preflight.RequiresMigration);
        Assert.Equal(2, preflight.TargetSchemaVersion);
        var progress = new CollectingProgress();

        var restore = await host.BackupServices.Restore.RestoreAsync(request, progress);

        Assert.Equal(RestoreStatus.Succeeded, restore.Status);
        Assert.True(File.Exists(packagePath));
        Assert.Contains(progress.Reports, item => item.Stage == RestoreProgressStage.Completed);
        await host.RefreshAfterRestoreAsync();
        Assert.Equal(1, restoreApplied);
        Assert.NotNull(host.EditingServices);
        var reopened = (await host.EditingServices!.Workspace.OpenAsync(noteId))!;
        Assert.Equal("original body", reopened.Body);
        var drafts = await host.EditingServices.DraftRecovery.ListAsync();
        Assert.Contains(drafts.Items, item => item.Draft.NoteId == noteId && item.IsPreRestore);
        Assert.Throws<ObjectDisposedException>(() => session.ChangeText("late", "late"));
    }

    [Fact]
    public async Task DeleteManualBackup_RemovesOnlyManualPackage()
    {
        using var temporary = new TestTemporaryDirectory();
        var backupDirectory = Path.Combine(temporary.RootPath, "backups");
        Directory.CreateDirectory(backupDirectory);
        await using var host = CreateHost(Path.Combine(temporary.RootPath, "data"));
        await host.StartAsync();
        var hostBackup = host.BackupServices!.Host;
        await hostBackup.SetBackupDirectoryAsync(backupDirectory);
        var manual = await hostBackup.CreateManualBackupAsync();
        Assert.Equal(BackupOperationStatus.Succeeded, manual.Status);

        var delete = await hostBackup.DeleteManualBackupAsync(manual.PackagePath!);

        Assert.Equal(BackupOperationStatus.Succeeded, delete.Status);
        Assert.False(File.Exists(manual.PackagePath));
        Assert.Empty((await hostBackup.ListBackupsAsync()).ValidEntries);
    }

    [Fact]
    public async Task Restore_SuspendedBackupsAreBusyUntilLeaseReleased()
    {
        using var temporary = new TestTemporaryDirectory();
        var backupDirectory = Path.Combine(temporary.RootPath, "backups");
        Directory.CreateDirectory(backupDirectory);
        await using var host = CreateHost(Path.Combine(temporary.RootPath, "data"));
        await host.StartAsync();
        var hostBackup = host.BackupServices!.Host;
        await hostBackup.SetBackupDirectoryAsync(backupDirectory);

        await using (await hostBackup.SuspendForRestoreAsync())
        {
            Assert.Equal(
                BackupOperationStatus.Busy,
                (await hostBackup.CreateManualBackupAsync()).Status);
        }

        Assert.Equal(
            BackupOperationStatus.Succeeded,
            (await hostBackup.CreateManualBackupAsync()).Status);
    }

    private static DesktopApplicationHost CreateHost(string dataRoot) =>
        new(
            new DesktopStartupOptions { DataRoot = dataRoot },
            new TestTimeProvider(Now));

    private sealed class CollectingProgress : IProgress<RestoreProgress>
    {
        public List<RestoreProgress> Reports { get; } = new();

        public void Report(RestoreProgress value) => Reports.Add(value);
    }
}
