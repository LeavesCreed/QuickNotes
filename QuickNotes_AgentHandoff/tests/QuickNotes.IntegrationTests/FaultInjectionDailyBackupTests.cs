using QuickNotes.Application;
using QuickNotes.Infrastructure.Backup;
using QuickNotes.Infrastructure.Sqlite;
using Xunit;

namespace QuickNotes.IntegrationTests;

public sealed class FaultInjectionDailyBackupTests
{
    private static readonly DateTimeOffset StartUtc =
        new(2026, 10, 28, 9, 30, 0, TimeSpan.Zero);

    private static readonly TimeZoneInfo LocalZone =
        TimeZoneInfo.CreateCustomTimeZone(
            "QuickNotes-B6-Test-Plus8",
            TimeSpan.FromHours(8),
            "QuickNotes-B6-Test-Plus8",
            "QuickNotes-B6-Test-Plus8");

    [Fact]
    public async Task Retention_DeleteFailure_KeepsBackupAndReportsWarning()
    {
        using var database = await TestDatabase.CreateAsync();
        var note = TestData.NewNote();
        await database.Notes.CreateAsync(note, Array.Empty<Guid>());
        using var temporary = new TemporaryDirectory();
        var backupDirectory = CreateBackupDirectory(temporary);
        var snapshotPath = Path.Combine(temporary.RootPath, "seed-snapshot.db");
        await new SqliteSnapshotProvider(database.DatabasePath).CreateAsync(snapshotPath);
        var seededPaths = new Dictionary<DateOnly, string>();
        for (var date = new DateOnly(2026, 10, 10);
             date <= new DateOnly(2026, 10, 27);
             date = date.AddDays(1))
        {
            var utc = new DateTimeOffset(date.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero);
            seededPaths[date] = await PublishPackageAsync(
                snapshotPath,
                backupDirectory,
                BackupType.Daily,
                utc);
        }

        var manualPath = await PublishPackageAsync(
            snapshotPath,
            backupDirectory,
            BackupType.Manual,
            new DateTimeOffset(2026, 10, 10, 10, 0, 0, TimeSpan.Zero));
        var readOnlyPath = seededPaths[new DateOnly(2026, 10, 10)];
        var deleteCandidatePath = seededPaths[new DateOnly(2026, 10, 12)];
        var weeklyKeptPath = seededPaths[new DateOnly(2026, 10, 11)];
        var time = new MutableTimeProvider(StartUtc, LocalZone);
        var service = CreateService(database.DatabasePath, time);
        try
        {
            File.SetAttributes(
                readOnlyPath,
                File.GetAttributes(readOnlyPath) | FileAttributes.ReadOnly);

            var result = await service.RunCheckAsync(backupDirectory);

            Assert.Equal(DailyBackupOutcome.BackedUp, result.Outcome);
            Assert.True(File.Exists(result.Backup!.PackagePath));
            Assert.True(File.Exists(readOnlyPath));
            Assert.False(File.Exists(deleteCandidatePath));
            Assert.True(File.Exists(weeklyKeptPath));
            Assert.True(File.Exists(manualPath));
            Assert.True(File.Exists(StatePath(backupDirectory)));
            var warning = Assert.Single(result.Retention!.Warnings);
            Assert.Equal("retention_delete_failed", warning.ErrorCode);
            Assert.Equal(readOnlyPath, warning.PackagePath);
            Assert.NotNull(await database.Notes.GetAsync(note.Id));
        }
        finally
        {
            File.SetAttributes(readOnlyPath, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task BackupDirectoryUnavailable_ReturnsDestinationUnavailable()
    {
        using var database = await TestDatabase.CreateAsync();
        var note = TestData.NewNote();
        await database.Notes.CreateAsync(note, Array.Empty<Guid>());
        using var temporary = new TemporaryDirectory();
        var backupPath = Path.Combine(temporary.RootPath, "backups");
        await File.WriteAllTextAsync(backupPath, "occupied");
        var time = new MutableTimeProvider(StartUtc, LocalZone);
        var service = CreateService(database.DatabasePath, time);

        var result = await service.RunCheckAsync(backupPath);

        Assert.Equal(DailyBackupOutcome.Failed, result.Outcome);
        Assert.Equal(BackupErrorCode.DestinationUnavailable, result.Error!.Code);
        Assert.True(result.Error.IsRetryable);
        Assert.True(File.Exists(backupPath));
        Assert.Equal("occupied", await File.ReadAllTextAsync(backupPath));
        Assert.NotNull(await database.Notes.GetAsync(note.Id));
    }

    private static DailyBackupService CreateService(
        string sourceDatabasePath,
        MutableTimeProvider timeProvider)
    {
        var catalog = new FileBackupCatalog();
        var coordinator = new BackupCoordinator(
            new SqliteSnapshotProvider(sourceDatabasePath),
            new ZipBackupPackageStore(),
            timeProvider);
        return new DailyBackupService(
            new SqliteChangeDetector(sourceDatabasePath),
            catalog,
            coordinator,
            new BackupRetentionPolicy(),
            new FileBackupRetentionStore(),
            new FileBackupStateStore(),
            timeProvider);
    }

    private static string CreateBackupDirectory(TemporaryDirectory temporary)
    {
        var backupDirectory = Path.Combine(temporary.RootPath, "backups");
        Directory.CreateDirectory(backupDirectory);
        return backupDirectory;
    }

    private static async Task<string> PublishPackageAsync(
        string snapshotPath,
        string backupDirectory,
        BackupType type,
        DateTimeOffset createdAtUtc)
    {
        var info = await new ZipBackupPackageStore().PublishAsync(
            snapshotPath,
            2,
            new BackupRequest
            {
                BackupDirectory = backupDirectory,
                Type = type
            },
            createdAtUtc,
            createdAtUtc.ToOffset(TimeSpan.FromHours(8)));
        return info.PackagePath;
    }

    private static string StatePath(string backupDirectory) =>
        Path.Combine(backupDirectory, FileBackupStateStore.StateFileName);
}
