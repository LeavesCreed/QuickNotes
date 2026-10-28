using QuickNotes.Application;
using QuickNotes.Infrastructure.Backup;
using QuickNotes.Infrastructure.Sqlite;
using Xunit;

namespace QuickNotes.IntegrationTests;

public sealed class FaultInjectionBackupTests
{
    [Fact]
    public async Task Backup_SnapshotDiskFull_CleansTempAndLeavesSourceUsable()
    {
        using var database = await TestDatabase.CreateAsync();
        var note = TestData.NewNote();
        await database.Notes.CreateAsync(note, Array.Empty<Guid>());
        using var temporary = new TemporaryDirectory();
        var backupDirectory = CreateBackupDirectory(temporary);
        var coordinator = new BackupCoordinator(
            new FaultingSnapshotProvider(),
            new ZipBackupPackageStore());

        var result = await coordinator.CreateAsync(new BackupRequest
        {
            BackupDirectory = backupDirectory
        });

        Assert.Equal(BackupStatus.SnapshotFailed, result.Status);
        Assert.Equal(BackupErrorCode.SnapshotFailed, result.Error!.Code);
        Assert.Empty(Directory.GetFiles(backupDirectory, "*.zip"));
        Assert.Empty(Directory.GetDirectories(backupDirectory, ".quicknotes-tmp-*"));
        Assert.NotNull(await database.Notes.GetAsync(note.Id));
    }

    [Fact]
    public async Task Backup_PackageDiskFull_ReturnsStorageUnavailableAndNoFinalPackage()
    {
        using var database = await TestDatabase.CreateAsync();
        var note = TestData.NewNote();
        await database.Notes.CreateAsync(note, Array.Empty<Guid>());
        using var temporary = new TemporaryDirectory();
        var backupDirectory = CreateBackupDirectory(temporary);
        var coordinator = new BackupCoordinator(
            new SqliteSnapshotProvider(database.DatabasePath),
            new FaultingPackageStore());

        var result = await coordinator.CreateAsync(new BackupRequest
        {
            BackupDirectory = backupDirectory
        });

        Assert.Equal(BackupStatus.StorageUnavailable, result.Status);
        Assert.Equal(BackupErrorCode.PackageWriteFailed, result.Error!.Code);
        Assert.Empty(Directory.GetFiles(backupDirectory, "*.zip"));
        Assert.Empty(Directory.GetDirectories(backupDirectory, ".quicknotes-tmp-*"));
        Assert.NotNull(await database.Notes.GetAsync(note.Id));
    }

    [Fact]
    public async Task Backup_CancelledDuringPackaging_ThrowsAndLeavesNoFinalPackage()
    {
        using var database = await TestDatabase.CreateAsync();
        var note = TestData.NewNote();
        await database.Notes.CreateAsync(note, Array.Empty<Guid>());
        using var temporary = new TemporaryDirectory();
        var backupDirectory = CreateBackupDirectory(temporary);
        var coordinator = new BackupCoordinator(
            new SqliteSnapshotProvider(database.DatabasePath),
            new CancelingPackageStore());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.CreateAsync(new BackupRequest
            {
                BackupDirectory = backupDirectory
            }));

        Assert.Empty(Directory.GetFiles(backupDirectory, "*.zip"));
        Assert.Empty(Directory.GetDirectories(backupDirectory, ".quicknotes-tmp-*"));
        Assert.Empty(Directory.GetFiles(backupDirectory, ".quicknotes-*"));
        Assert.NotNull(await database.Notes.GetAsync(note.Id));
    }

    [Fact]
    public async Task Backup_CorruptPackage_VerifierRejectsChecksumMismatch()
    {
        using var database = await TestDatabase.CreateAsync();
        await database.Notes.CreateAsync(TestData.NewNote(), Array.Empty<Guid>());
        using var temporary = new TemporaryDirectory();
        var backupDirectory = CreateBackupDirectory(temporary);
        var snapshotPath = Path.Combine(temporary.RootPath, "snapshot.db");
        await new SqliteSnapshotProvider(database.DatabasePath).CreateAsync(snapshotPath);
        var utcNow = DateTimeOffset.UtcNow;
        var package = await new ZipBackupPackageStore().PublishAsync(
            snapshotPath,
            1,
            new BackupRequest
            {
                BackupDirectory = backupDirectory,
                Type = BackupType.Manual
            },
            utcNow,
            TimeZoneInfo.ConvertTime(utcNow, TimeZoneInfo.Local));
        FaultInjectionTestData.CorruptDatabaseEntry(package.PackagePath);
        var verificationDirectory = Path.Combine(temporary.RootPath, "verify");
        Directory.CreateDirectory(verificationDirectory);

        var exception = await Assert.ThrowsAsync<BackupException>(
            () => new BackupPackageVerifier().VerifyAsync(
                package.PackagePath,
                verificationDirectory));

        Assert.Equal(BackupErrorCodes.PackageVerificationFailed, exception.ErrorCode);
        Assert.Empty(Directory.GetFiles(verificationDirectory, ".quicknotes-verify-*"));
    }

    [Fact]
    public async Task Backup_DirectoryUnavailable_ReturnsDestinationUnavailable()
    {
        using var database = await TestDatabase.CreateAsync();
        var note = TestData.NewNote();
        await database.Notes.CreateAsync(note, Array.Empty<Guid>());
        using var temporary = new TemporaryDirectory();
        var backupPath = Path.Combine(temporary.RootPath, "backups");
        await File.WriteAllTextAsync(backupPath, "not a directory");
        var coordinator = new BackupCoordinator(
            new SqliteSnapshotProvider(database.DatabasePath),
            new ZipBackupPackageStore());

        var result = await coordinator.CreateAsync(new BackupRequest
        {
            BackupDirectory = backupPath
        });

        Assert.Equal(BackupStatus.StorageUnavailable, result.Status);
        Assert.Equal(BackupErrorCode.DestinationUnavailable, result.Error!.Code);
        Assert.NotNull(await database.Notes.GetAsync(note.Id));
    }

    private static string CreateBackupDirectory(TemporaryDirectory temporary)
    {
        var backupDirectory = Path.Combine(temporary.RootPath, "backups");
        Directory.CreateDirectory(backupDirectory);
        return backupDirectory;
    }
}
