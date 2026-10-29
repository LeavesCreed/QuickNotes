using QuickNotes.Application;
using Xunit;

namespace QuickNotes.UnitTests;

public sealed class BackupRuntimeStatusTests
{
    [Fact]
    public void NotConfigured_HasCurrentVersionAndNoBackupDirectory()
    {
        var now = new DateTimeOffset(2026, 10, 28, 9, 30, 0, TimeSpan.Zero);

        var status = BackupRuntimeStatus.NotConfigured(now);

        Assert.Equal(BackupRuntimeStatus.CurrentFormatVersion, status.FormatVersion);
        Assert.Equal(now, status.LastCheckAtUtc);
        Assert.False(status.AutoBackupConfigured);
        Assert.Null(status.BackupDirectory);
    }

    [Fact]
    public void RestoreProgress_ExposesCancelability()
    {
        var progress = new RestoreProgress(
            RestoreProgressStage.Switching,
            "Switching the database.",
            IsCancelable: false);

        Assert.Equal(RestoreProgressStage.Switching, progress.Stage);
        Assert.False(progress.IsCancelable);
    }
}
