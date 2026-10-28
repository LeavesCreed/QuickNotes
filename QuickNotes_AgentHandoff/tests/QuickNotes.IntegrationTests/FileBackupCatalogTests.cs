using System.Globalization;
using System.IO.Compression;
using QuickNotes.Application;
using QuickNotes.Infrastructure.Backup;
using QuickNotes.Infrastructure.Sqlite;
using Xunit;

namespace QuickNotes.IntegrationTests;

public sealed class FileBackupCatalogTests
{
    private static readonly DateTimeOffset DailyUtc =
        new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset ManualUtc =
        new(2026, 10, 4, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReadForScheduling_IncludesDailyAndManualPackagesWithLocalDates()
    {
        using var database = await TestDatabase.CreateAsync();
        await database.Notes.CreateAsync(TestData.NewNote(), Array.Empty<Guid>());
        using var temporary = new TemporaryDirectory();
        var backupDirectory = Path.Combine(temporary.RootPath, "backups");
        Directory.CreateDirectory(backupDirectory);
        var snapshotPath = Path.Combine(temporary.RootPath, "snapshot.db");
        await new SqliteSnapshotProvider(database.DatabasePath).CreateAsync(snapshotPath);
        var store = new ZipBackupPackageStore();
        var dailyLocal = DailyUtc.ToOffset(TimeSpan.FromHours(8));
        var manualLocal = ManualUtc.ToOffset(TimeSpan.FromHours(8));
        await store.PublishAsync(
            snapshotPath,
            2,
            new BackupRequest { BackupDirectory = backupDirectory, Type = BackupType.Daily },
            DailyUtc,
            dailyLocal);
        await store.PublishAsync(
            snapshotPath,
            2,
            new BackupRequest { BackupDirectory = backupDirectory, Type = BackupType.Manual },
            ManualUtc,
            manualLocal);
        var catalog = new FileBackupCatalog();

        var result = await catalog.ReadForSchedulingAsync(backupDirectory);

        Assert.Equal(2, result.ValidEntries.Count);
        Assert.Empty(result.Issues);
        var daily = result.ValidEntries.Single(
            entry => entry.Manifest.BackupType == BackupType.Daily);
        var manual = result.ValidEntries.Single(
            entry => entry.Manifest.BackupType == BackupType.Manual);
        Assert.Equal(new DateOnly(2026, 10, 5), daily.LocalDate);
        Assert.Equal(new DateOnly(2026, 10, 4), manual.LocalDate);
        Assert.Equal(ISOWeek.GetYear(dailyLocal.DateTime), daily.IsoWeekYear);
        Assert.Equal(ISOWeek.GetWeekOfYear(dailyLocal.DateTime), daily.IsoWeek);
        Assert.Equal(DailyUtc, result.ValidEntries[0].Manifest.CreatedAtUtc);
    }

    [Fact]
    public async Task ReadForScheduling_IgnoresOtherFilesAndReportsCorruptPackages()
    {
        using var database = await TestDatabase.CreateAsync();
        await database.Notes.CreateAsync(TestData.NewNote(), Array.Empty<Guid>());
        using var temporary = new TemporaryDirectory();
        var backupDirectory = Path.Combine(temporary.RootPath, "backups");
        Directory.CreateDirectory(backupDirectory);
        var snapshotPath = Path.Combine(temporary.RootPath, "snapshot.db");
        await new SqliteSnapshotProvider(database.DatabasePath).CreateAsync(snapshotPath);
        await new ZipBackupPackageStore().PublishAsync(
            snapshotPath,
            2,
            new BackupRequest { BackupDirectory = backupDirectory, Type = BackupType.Daily },
            DailyUtc,
            DailyUtc.ToOffset(TimeSpan.FromHours(8)));
        await File.WriteAllTextAsync(
            Path.Combine(backupDirectory, "quicknotes-corrupt.zip"),
            "not a zip");
        await File.WriteAllTextAsync(
            Path.Combine(backupDirectory, "readme.txt"),
            "ignored");
        await File.WriteAllTextAsync(
            Path.Combine(backupDirectory, FileBackupStateStore.StateFileName),
            "{}");

        var result = await new FileBackupCatalog().ReadForSchedulingAsync(backupDirectory);

        Assert.Single(result.ValidEntries);
        var issue = Assert.Single(result.Issues);
        Assert.EndsWith("quicknotes-corrupt.zip", issue.PackagePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadForRetention_RejectsTamperedDatabaseEntryThatSchedulingAccepts()
    {
        using var database = await TestDatabase.CreateAsync();
        await database.Notes.CreateAsync(TestData.NewNote(), Array.Empty<Guid>());
        using var temporary = new TemporaryDirectory();
        var backupDirectory = Path.Combine(temporary.RootPath, "backups");
        Directory.CreateDirectory(backupDirectory);
        var snapshotPath = Path.Combine(temporary.RootPath, "snapshot.db");
        await new SqliteSnapshotProvider(database.DatabasePath).CreateAsync(snapshotPath);
        var info = await new ZipBackupPackageStore().PublishAsync(
            snapshotPath,
            2,
            new BackupRequest { BackupDirectory = backupDirectory, Type = BackupType.Daily },
            DailyUtc,
            DailyUtc.ToOffset(TimeSpan.FromHours(8)));
        TamperEntryPreservingLength(info.PackagePath, BackupManifest.DatabaseEntryName);
        var catalog = new FileBackupCatalog();

        var scheduling = await catalog.ReadForSchedulingAsync(backupDirectory);
        var retention = await catalog.ReadForRetentionAsync(backupDirectory);

        Assert.Single(scheduling.ValidEntries);
        Assert.Empty(scheduling.Issues);
        Assert.Empty(retention.ValidEntries);
        Assert.Single(retention.Issues);
    }

    [Fact]
    public async Task Read_WhenDirectoryMissing_ThrowsDestinationUnavailable()
    {
        using var temporary = new TemporaryDirectory();

        var exception = await Assert.ThrowsAsync<BackupException>(
            () => new FileBackupCatalog().ReadForSchedulingAsync(
                Path.Combine(temporary.RootPath, "missing")));

        Assert.Equal(BackupErrorCodes.DestinationUnavailable, exception.ErrorCode);
        Assert.Equal("catalog", exception.Operation);
    }

    private static void TamperEntryPreservingLength(string packagePath, string entryName)
    {
        byte[] bytes;
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Read))
        {
            var entry = archive.GetEntry(entryName)!;
            using var stream = entry.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            bytes = memory.ToArray();
        }

        bytes[0] ^= 0xFF;
        using var update = ZipFile.Open(packagePath, ZipArchiveMode.Update);
        update.GetEntry(entryName)?.Delete();
        var replacement = update.CreateEntry(entryName);
        using var output = replacement.Open();
        output.Write(bytes);
    }
}
