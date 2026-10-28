using System.Globalization;
using QuickNotes.Application;
using QuickNotes.Infrastructure.Backup;
using Xunit;

namespace QuickNotes.IntegrationTests;

public sealed class FileBackupRetentionStoreTests
{
    [Fact]
    public async Task DeleteAsync_DeletesOnlyListedPackages()
    {
        using var temporary = new TemporaryDirectory();
        var first = Path.Combine(temporary.RootPath, "first.zip");
        var second = Path.Combine(temporary.RootPath, "second.zip");
        var keep = Path.Combine(temporary.RootPath, "keep.txt");
        await File.WriteAllTextAsync(first, "first");
        await File.WriteAllTextAsync(second, "second");
        await File.WriteAllTextAsync(keep, "keep");
        var store = new FileBackupRetentionStore();

        var result = await store.DeleteAsync(
            temporary.RootPath,
            new[] { Entry(first), Entry(second) });

        Assert.Equal(2, result.DeletedPackagePaths.Count);
        Assert.Empty(result.Warnings);
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
        Assert.True(File.Exists(keep));
    }

    [Fact]
    public async Task DeleteAsync_DoesNotDeleteManualPackages()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.RootPath, "manual.zip");
        await File.WriteAllTextAsync(path, "manual");
        var store = new FileBackupRetentionStore();

        var result = await store.DeleteAsync(
            temporary.RootPath,
            new[] { Entry(path, BackupType.Manual) });

        Assert.Empty(result.DeletedPackagePaths);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("retention_unsafe_type", warning.ErrorCode);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task DeleteAsync_RejectsPathsOutsideBackupDirectory()
    {
        using var temporary = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        var outsidePath = Path.Combine(outside.RootPath, "outside.zip");
        await File.WriteAllTextAsync(outsidePath, "outside");
        var store = new FileBackupRetentionStore();

        var result = await store.DeleteAsync(
            temporary.RootPath,
            new[] { Entry(outsidePath) });

        Assert.Empty(result.DeletedPackagePaths);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("retention_unsafe_path", warning.ErrorCode);
        Assert.True(File.Exists(outsidePath));
    }

    [Fact]
    public async Task DeleteAsync_RejectsNonZipPaths()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.RootPath, "notes.txt");
        await File.WriteAllTextAsync(path, "keep");
        var store = new FileBackupRetentionStore();

        var result = await store.DeleteAsync(
            temporary.RootPath,
            new[] { Entry(path) });

        Assert.Empty(result.DeletedPackagePaths);
        Assert.Single(result.Warnings);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task DeleteAsync_WhenDirectoryMissing_ReturnsWarning()
    {
        using var temporary = new TemporaryDirectory();
        var missing = Path.Combine(temporary.RootPath, "missing");

        var result = await new FileBackupRetentionStore().DeleteAsync(
            missing,
            new[] { Entry(Path.Combine(missing, "daily.zip")) });

        Assert.Empty(result.DeletedPackagePaths);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("retention_directory_unavailable", warning.ErrorCode);
    }

    [Fact]
    public async Task DeleteAsync_WhenDeletionFails_ReturnsWarningAndContinues()
    {
        using var temporary = new TemporaryDirectory();
        var directoryAsZip = Path.Combine(temporary.RootPath, "locked.zip");
        Directory.CreateDirectory(directoryAsZip);
        var remaining = Path.Combine(temporary.RootPath, "remaining.zip");
        await File.WriteAllTextAsync(remaining, "remaining");
        var store = new FileBackupRetentionStore();

        var result = await store.DeleteAsync(
            temporary.RootPath,
            new[] { Entry(directoryAsZip), Entry(remaining) });

        Assert.Contains(remaining, result.DeletedPackagePaths);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("retention_delete_failed", warning.ErrorCode);
        Assert.True(Directory.Exists(directoryAsZip));
        Assert.False(File.Exists(remaining));
    }

    private static BackupCatalogEntry Entry(
        string path,
        BackupType type = BackupType.Daily)
    {
        var utc = new DateTimeOffset(2026, 10, 28, 9, 30, 0, TimeSpan.Zero);
        var local = utc.ToOffset(TimeSpan.FromHours(8));
        var manifest = new BackupManifest
        {
            FormatVersion = BackupManifest.CurrentFormatVersion,
            BackupType = type,
            CreatedAtUtc = utc,
            CreatedAtLocal = local,
            SchemaVersion = 1,
            DatabaseFileName = BackupManifest.DatabaseEntryName,
            DatabaseSizeBytes = 4096,
            DatabaseSha256 = new string('a', 64)
        };
        return new BackupCatalogEntry
        {
            PackagePath = path,
            Manifest = manifest,
            LocalDate = DateOnly.FromDateTime(local.DateTime),
            IsoWeekYear = ISOWeek.GetYear(local.DateTime),
            IsoWeek = ISOWeek.GetWeekOfYear(local.DateTime)
        };
    }
}
