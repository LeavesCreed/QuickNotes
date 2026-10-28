using QuickNotes.Desktop.Paths;

namespace QuickNotes.Desktop.Tests;

public sealed class DesktopPathsTests
{
    [Fact]
    public void CreateDefault_UsesQuickNotesUnderLocalAppData()
    {
        var paths = DesktopPaths.CreateDefault();

        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QuickNotes"),
            paths.DataRoot);
    }

    [Fact]
    public void Create_WithRoot_UsesExpectedPaths()
    {
        using var temporary = new TestTemporaryDirectory();

        var paths = DesktopPaths.Create(temporary.RootPath);

        Assert.Equal(Path.GetFullPath(temporary.RootPath), paths.DataRoot);
        Assert.Equal(Path.Combine(paths.DataRoot, "notes.db"), paths.DatabasePath);
        Assert.Equal(Path.Combine(paths.DataRoot, "drafts"), paths.DraftDirectory);
        Assert.Equal(Path.Combine(paths.DataRoot, "logs"), paths.LogDirectory);
        Assert.Equal(Path.Combine(paths.DataRoot, "application-state.json"), paths.StatePath);
        Assert.Equal(Path.Combine(paths.DataRoot, "recovery-journal.json"), paths.RestoreJournalPath);
        Assert.Equal(Path.Combine(paths.DataRoot, "device-settings.json"), paths.DeviceSettingsPath);
        Assert.Equal(Path.Combine(paths.DataRoot, "migration-protection"), paths.MigrationProtectionDirectory);
    }

    [Fact]
    public void Create_WithEmptyRoot_Throws()
    {
        Assert.Throws<ArgumentException>(() => DesktopPaths.Create(" "));
    }

    [Fact]
    public void EnsureDirectories_CreatesRequiredDirectories()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(temporary.RootPath);

        paths.EnsureDirectories();

        Assert.True(Directory.Exists(paths.DataRoot));
        Assert.True(Directory.Exists(paths.DraftDirectory));
        Assert.True(Directory.Exists(paths.LogDirectory));
    }
}
