using QuickNotes.Application;
using QuickNotes.Domain;
using QuickNotes.Infrastructure.Backup;
using QuickNotes.Infrastructure.Drafts;
using QuickNotes.Infrastructure.Sqlite;
using Xunit;

namespace QuickNotes.IntegrationTests;

public sealed class FaultInjectionRestoreTests
{
    [Fact]
    public async Task Restore_StagingDiskFull_PreservesProtectionAndCurrentDatabase()
    {
        using var current = await TestDatabase.CreateAsync();
        var currentNote = TestData.NewNote() with { Body = "keep me" };
        await current.Notes.CreateAsync(currentNote, Array.Empty<Guid>());
        using var temporary = new TemporaryDirectory();
        var (package, backupDirectory, backupNote) = await CreateManualBackupAsync(temporary);
        var gate = new DatabaseWriteGate();
        var coordinator = CreateCoordinator(
            new StagingFailingExtractor(new BackupPackageVerifier()),
            new SqliteRestoreStore(),
            current.DatabasePath,
            backupDirectory,
            new FileNoteDraftStore(temporary.DraftDirectory),
            gate);

        var result = await coordinator.RestoreAsync(
            CreateRequest(package.PackagePath, current.DatabasePath, backupDirectory));

        Assert.Equal(RestoreStatus.VerificationFailed, result.Status);
        Assert.Equal(RestoreErrorCode.StagingUnavailable, result.Error!.Code);
        Assert.NotNull(result.ProtectionSnapshotPath);
        Assert.True(File.Exists(result.ProtectionSnapshotPath));
        Assert.Equal("keep me", (await current.Notes.GetAsync(currentNote.Id))!.Body);
        Assert.Null(await current.Notes.GetAsync(backupNote.Id));
        Assert.Empty(StagingDirectories(current.DatabasePath));
        Assert.False(gate.IsBlocked);
    }

    [Fact]
    public async Task Restore_SwitchLocked_ReturnsSwitchFailedAndCurrentUnchanged()
    {
        using var current = await TestDatabase.CreateAsync();
        var currentNote = TestData.NewNote() with { Body = "keep me" };
        await current.Notes.CreateAsync(currentNote, Array.Empty<Guid>());
        using var temporary = new TemporaryDirectory();
        var (package, backupDirectory, backupNote) = await CreateManualBackupAsync(temporary);
        var draftStore = new FileNoteDraftStore(temporary.DraftDirectory);
        var gate = new DatabaseWriteGate();
        var coordinator = CreateCoordinator(
            new BackupPackageVerifier(),
            new LockingRestoreStore(new SqliteRestoreStore()),
            current.DatabasePath,
            backupDirectory,
            draftStore,
            gate);

        var result = await coordinator.RestoreAsync(
            CreateRequest(package.PackagePath, current.DatabasePath, backupDirectory));

        Assert.Equal(RestoreStatus.SwitchFailed, result.Status);
        Assert.Equal(RestoreErrorCode.SwitchFailed, result.Error!.Code);
        Assert.False(result.WritesBlocked);
        Assert.NotNull(result.ProtectionSnapshotPath);
        Assert.True(File.Exists(result.ProtectionSnapshotPath));
        Assert.Equal("keep me", (await current.Notes.GetAsync(currentNote.Id))!.Body);
        Assert.Null(await current.Notes.GetAsync(backupNote.Id));
        Assert.Empty(StagingDirectories(current.DatabasePath));
        Assert.False(gate.IsBlocked);

        var saveCoordinator = CreateSaveCoordinator(current, draftStore, gate);
        var saved = await saveCoordinator.CreateAsync(TestData.NewNote(), Array.Empty<Guid>());
        Assert.Equal(SaveStatus.Saved, saved.Status);
    }

    [Fact]
    public async Task Restore_RollbackFailure_BlocksWritesAndPreservesRecoveryFiles()
    {
        using var current = await TestDatabase.CreateAsync();
        var currentNote = TestData.NewNote() with { Body = "keep me" };
        await current.Notes.CreateAsync(currentNote, Array.Empty<Guid>());
        using var temporary = new TemporaryDirectory();
        var (package, backupDirectory, _) = await CreateManualBackupAsync(temporary);
        var draftStore = new FileNoteDraftStore(temporary.DraftDirectory);
        var gate = new DatabaseWriteGate();
        var coordinator = CreateCoordinator(
            new BackupPackageVerifier(),
            new RollbackFailingRestoreStore(new SqliteRestoreStore(), current.DatabasePath),
            current.DatabasePath,
            backupDirectory,
            draftStore,
            gate);

        var result = await coordinator.RestoreAsync(
            CreateRequest(package.PackagePath, current.DatabasePath, backupDirectory));

        Assert.Equal(RestoreStatus.RollbackFailed, result.Status);
        Assert.Equal(RestoreErrorCode.RollbackFailed, result.Error!.Code);
        Assert.True(result.WritesBlocked);
        Assert.True(gate.IsBlocked);
        Assert.NotNull(result.RecoveryHint);
        Assert.Contains(current.DatabasePath, result.RecoveryHint);
        Assert.NotNull(result.ProtectionSnapshotPath);
        Assert.True(File.Exists(result.ProtectionSnapshotPath));
        var staging = Assert.Single(StagingDirectories(current.DatabasePath));
        Assert.True(File.Exists(Path.Combine(staging, "rollback.db")));

        var saveCoordinator = CreateSaveCoordinator(current, draftStore, gate);
        var saved = await saveCoordinator.CreateAsync(TestData.NewNote(), Array.Empty<Guid>());

        Assert.Equal(SaveStatus.StorageUnavailable, saved.Status);
        Assert.Equal("database_writes_blocked", saved.Error!.Diagnostics);
    }

    private static async Task<(BackupPackageInfo Package, string BackupDirectory, Note BackupNote)>
        CreateManualBackupAsync(TemporaryDirectory temporary)
    {
        using var backupDatabase = await TestDatabase.CreateAsync();
        var backupNote = TestData.NewNote() with { Body = "restored body" };
        await backupDatabase.Notes.CreateAsync(backupNote, Array.Empty<Guid>());
        var backupDirectory = Path.Combine(temporary.RootPath, "backups");
        Directory.CreateDirectory(backupDirectory);
        var snapshotPath = Path.Combine(temporary.RootPath, "snapshot.db");
        await new SqliteSnapshotProvider(backupDatabase.DatabasePath).CreateAsync(snapshotPath);
        var utcNow = DateTimeOffset.UtcNow;
        var package = await new ZipBackupPackageStore().PublishAsync(
            snapshotPath,
            2,
            new BackupRequest
            {
                BackupDirectory = backupDirectory,
                Type = BackupType.Manual
            },
            utcNow,
            TimeZoneInfo.ConvertTime(utcNow, TimeZoneInfo.Local));
        return (package, backupDirectory, backupNote);
    }

    private static RestoreCoordinator CreateCoordinator(
        IBackupPackageExtractor extractor,
        IRestoreStore store,
        string currentDatabasePath,
        string backupDirectory,
        INoteDraftStore draftStore,
        DatabaseWriteGate gate) =>
        new(
            extractor,
            store,
            new BackupCoordinator(
                new SqliteSnapshotProvider(currentDatabasePath),
                new ZipBackupPackageStore()),
            draftStore,
            new DriveInfoDiskSpaceProvider(),
            gate);

    private static RestoreRequest CreateRequest(
        string packagePath,
        string currentDatabasePath,
        string backupDirectory) => new()
    {
        PackagePath = packagePath,
        CurrentDatabasePath = currentDatabasePath,
        BackupDirectory = backupDirectory,
        AllowPendingDrafts = true
    };

    private static NoteSaveCoordinator CreateSaveCoordinator(
        TestDatabase database,
        INoteDraftStore draftStore,
        DatabaseWriteGate gate) =>
        new(
            database.Notes,
            draftStore,
            new SaveRetryOptions
            {
                RetryDelays = new[] { TimeSpan.Zero, TimeSpan.Zero }
            },
            logger: null,
            writeGate: gate);

    private static string[] StagingDirectories(string currentDatabasePath) =>
        Directory.GetDirectories(
            Path.GetDirectoryName(Path.GetFullPath(currentDatabasePath))!,
            ".quicknotes-restore-*");
}
