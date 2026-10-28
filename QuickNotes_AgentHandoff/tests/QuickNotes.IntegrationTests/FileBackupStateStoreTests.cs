using System.Globalization;
using QuickNotes.Application;
using QuickNotes.Infrastructure.Backup;
using Xunit;

namespace QuickNotes.IntegrationTests;

public sealed class FileBackupStateStoreTests
{
    private static readonly DateTimeOffset UtcNow =
        new(2026, 10, 28, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task WriteAndRead_RoundTripsAtomically()
    {
        using var temporary = new TemporaryDirectory();
        var store = new FileBackupStateStore();
        var state = NewState("schema=1;notes=0");

        await store.WriteAsync(temporary.RootPath, state);

        Assert.Equal(state, await store.ReadAsync(temporary.RootPath));
        var path = Path.Combine(temporary.RootPath, FileBackupStateStore.StateFileName);
        Assert.True(File.Exists(path));
        var json = await File.ReadAllTextAsync(path);
        Assert.Contains("\"lastSuccessfulFingerprint\"", json, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(temporary.RootPath, "*.tmp"));
    }

    [Fact]
    public async Task Write_OverwritesPreviousState()
    {
        using var temporary = new TemporaryDirectory();
        var store = new FileBackupStateStore();
        await store.WriteAsync(temporary.RootPath, NewState("first"));

        var replacement = NewState("second");
        await store.WriteAsync(temporary.RootPath, replacement);

        Assert.Equal(replacement, await store.ReadAsync(temporary.RootPath));
        Assert.Empty(Directory.GetFiles(temporary.RootPath, "*.tmp"));
    }

    [Fact]
    public async Task Read_Missing_ReturnsNull()
    {
        using var temporary = new TemporaryDirectory();

        Assert.Null(await new FileBackupStateStore().ReadAsync(temporary.RootPath));
    }

    [Fact]
    public async Task Read_CorruptOrUnsupportedState_ReturnsNull()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.RootPath, FileBackupStateStore.StateFileName);
        await File.WriteAllTextAsync(path, "not json");

        Assert.Null(await new FileBackupStateStore().ReadAsync(temporary.RootPath));

        await File.WriteAllTextAsync(path, """
            {
              "formatVersion": 2,
              "lastSuccessfulFingerprint": "schema=1",
              "lastSuccessfulDailyAtUtc": "2026-10-28T09:30:00.0000000+00:00",
              "lastSuccessfulDailyLocalDate": "2026-10-28"
            }
            """);

        Assert.Null(await new FileBackupStateStore().ReadAsync(temporary.RootPath));
    }

    [Fact]
    public async Task Write_WhenDirectoryMissing_ThrowsDestinationUnavailable()
    {
        using var temporary = new TemporaryDirectory();
        var missing = Path.Combine(temporary.RootPath, "missing");

        var exception = await Assert.ThrowsAsync<BackupException>(
            () => new FileBackupStateStore().WriteAsync(missing, NewState("schema=1")));

        Assert.Equal(BackupErrorCodes.DestinationUnavailable, exception.ErrorCode);
        Assert.Equal("state", exception.Operation);
    }

    private static BackupScheduleState NewState(string fingerprint) => new()
    {
        FormatVersion = BackupScheduleState.CurrentFormatVersion,
        LastSuccessfulFingerprint = fingerprint,
        LastSuccessfulDailyAtUtc = UtcNow,
        LastSuccessfulDailyLocalDate = DateOnly.FromDateTime(
            UtcNow.ToOffset(TimeSpan.FromHours(8)).DateTime)
    };
}
