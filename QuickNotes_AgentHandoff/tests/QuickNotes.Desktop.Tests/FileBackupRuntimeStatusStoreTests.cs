using QuickNotes.Application;
using QuickNotes.Desktop.Backup;

namespace QuickNotes.Desktop.Tests;

public sealed class FileBackupRuntimeStatusStoreTests
{
    [Fact]
    public async Task WriteRead_RoundTripsAndLeavesNoTemporaryFile()
    {
        using var temporary = new TestTemporaryDirectory();
        var path = Path.Combine(temporary.RootPath, "backup-status.json");
        var store = new FileBackupRuntimeStatusStore(path);
        var status = new BackupRuntimeStatus
        {
            FormatVersion = BackupRuntimeStatus.CurrentFormatVersion,
            BackupDirectory = Path.Combine(temporary.RootPath, "backups"),
            LastCheckAtUtc = DateTimeOffset.UtcNow,
            LastSuccessAtUtc = DateTimeOffset.UtcNow,
            LastSuccessfulPackagePath = "quicknotes-test.zip",
            AutoBackupConfigured = true
        };

        await store.WriteAsync(status);
        var read = await store.ReadAsync();

        Assert.NotNull(read);
        Assert.Equal(status.BackupDirectory, read!.BackupDirectory);
        Assert.Equal(status.LastSuccessfulPackagePath, read.LastSuccessfulPackagePath);
        Assert.Empty(Directory.GetFiles(temporary.RootPath, "*.tmp"));
    }

    [Fact]
    public async Task Read_MissingCorruptOrUnknownVersion_ReturnsNull()
    {
        using var temporary = new TestTemporaryDirectory();
        var path = Path.Combine(temporary.RootPath, "backup-status.json");
        var store = new FileBackupRuntimeStatusStore(path);

        Assert.Null(await store.ReadAsync());

        await File.WriteAllTextAsync(path, "{");
        Assert.Null(await store.ReadAsync());

        await File.WriteAllTextAsync(path, """{"formatVersion":99}""");
        Assert.Null(await store.ReadAsync());
    }
}
