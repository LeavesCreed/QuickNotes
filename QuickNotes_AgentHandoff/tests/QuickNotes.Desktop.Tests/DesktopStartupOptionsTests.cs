using QuickNotes.Desktop.Hosting;

namespace QuickNotes.Desktop.Tests;

public sealed class DesktopStartupOptionsTests
{
    [Fact]
    public void Parse_NoArguments_UsesDefaults()
    {
        var options = DesktopStartupOptions.Parse(Array.Empty<string>());

        Assert.Null(options.DataRoot);
        Assert.Null(options.BackupDirectory);
        Assert.False(options.SingleInstanceProbe);
    }

    [Fact]
    public void Parse_DataRootAndBackupDirectory()
    {
        var options = DesktopStartupOptions.Parse(
            new[] { "--data-root", @"C:\data", "--backup-dir", @"C:\backups" });

        Assert.Equal(@"C:\data", options.DataRoot);
        Assert.Equal(@"C:\backups", options.BackupDirectory);
    }

    [Fact]
    public void Parse_StartupFlag_EnablesLoginStartupMode()
    {
        var startup = DesktopStartupOptions.Parse(new[] { "--startup" });
        var background = DesktopStartupOptions.Parse(new[] { "--background" });

        Assert.True(startup.StartOnLogin);
        Assert.True(background.StartOnLogin);
    }

    [Fact]
    public void Parse_SingleInstanceProbe()
    {
        var options = DesktopStartupOptions.Parse(new[] { "--single-instance-probe" });

        Assert.True(options.SingleInstanceProbe);
    }

    [Fact]
    public void Parse_MissingValue_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => DesktopStartupOptions.Parse(new[] { "--data-root" }));
    }

    [Fact]
    public void Parse_UnknownArgument_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => DesktopStartupOptions.Parse(new[] { "--unknown" }));
    }
}
