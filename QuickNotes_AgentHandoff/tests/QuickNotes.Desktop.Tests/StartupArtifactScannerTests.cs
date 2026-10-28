using QuickNotes.Desktop.Paths;
using QuickNotes.Desktop.Startup;

namespace QuickNotes.Desktop.Tests;

public sealed class StartupArtifactScannerTests
{
    [Fact]
    public void Scan_NoArtifacts_ReturnsEmpty()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(temporary.RootPath);
        paths.EnsureDirectories();
        File.WriteAllText(Path.Combine(paths.DataRoot, ".quicknotes-instance.lock"), "lock");

        var inventory = new StartupArtifactScanner(paths).Scan();

        Assert.Empty(inventory.Artifacts);
        Assert.False(inventory.HasDataEvidence);
    }

    [Fact]
    public void Scan_RestoreDirectoryWithRollback_IsRetained()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(temporary.RootPath);
        paths.EnsureDirectories();
        var restoreDirectory = Path.Combine(paths.DataRoot, ".quicknotes-restore-abc");
        Directory.CreateDirectory(restoreDirectory);
        var rollbackPath = Path.Combine(restoreDirectory, "rollback.db");
        File.WriteAllText(rollbackPath, "rollback");

        var inventory = new StartupArtifactScanner(paths).Scan();

        var artifact = Assert.Single(inventory.Artifacts);
        Assert.Equal(StartupArtifactKind.RestoreDirectory, artifact.Kind);
        Assert.True(Directory.Exists(restoreDirectory));
        Assert.True(File.Exists(rollbackPath));
    }

    [Fact]
    public void Scan_SidecarAndDraft_AreReported()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(temporary.RootPath);
        paths.EnsureDirectories();
        File.WriteAllText(paths.DatabasePath + "-wal", "wal");
        File.WriteAllText(Path.Combine(paths.DraftDirectory, "draft.json"), "{}");

        var inventory = new StartupArtifactScanner(paths).Scan();

        Assert.Contains(inventory.Artifacts, artifact => artifact.Kind == StartupArtifactKind.Sidecar);
        Assert.Contains(inventory.Artifacts, artifact => artifact.Kind == StartupArtifactKind.DraftFile);
        Assert.True(File.Exists(paths.DatabasePath + "-wal"));
    }

    [Fact]
    public void Scan_QuarantinedDatabase_IsRetained()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(temporary.RootPath);
        paths.EnsureDirectories();
        var quarantinePath = Path.Combine(paths.DataRoot, ".quicknotes-quarantine-abc.db");
        File.WriteAllText(quarantinePath, "corrupt");

        var inventory = new StartupArtifactScanner(paths).Scan();

        var artifact = Assert.Single(inventory.Artifacts);
        Assert.Equal(StartupArtifactKind.QuarantinedDatabase, artifact.Kind);
        Assert.True(File.Exists(quarantinePath));
    }

    [Fact]
    public void Scan_BackupTempsAndProtectionSnapshot_AreReported()
    {
        using var temporary = new TestTemporaryDirectory();
        var paths = DesktopPaths.Create(temporary.RootPath);
        paths.EnsureDirectories();
        var backupDirectory = Path.Combine(temporary.RootPath, "backups");
        Directory.CreateDirectory(backupDirectory);
        Directory.CreateDirectory(Path.Combine(backupDirectory, ".quicknotes-tmp-abc"));
        File.WriteAllText(Path.Combine(backupDirectory, ".quicknotes-package-abc.tmp"), "tmp");
        File.WriteAllText(Path.Combine(backupDirectory, ".quicknotes-verify-abc.db"), "verify");
        File.WriteAllText(Path.Combine(backupDirectory, "quicknotes-20261028-093000-000-manual.zip"), "zip");

        var inventory = new StartupArtifactScanner(paths, backupDirectory).Scan();

        Assert.Contains(inventory.Artifacts, artifact => artifact.Kind == StartupArtifactKind.BackupTempDirectory);
        Assert.Contains(inventory.Artifacts, artifact => artifact.Kind == StartupArtifactKind.PackageTempFile);
        Assert.Contains(inventory.Artifacts, artifact => artifact.Kind == StartupArtifactKind.VerifyTempFile);
        Assert.Contains(inventory.Artifacts, artifact => artifact.Kind == StartupArtifactKind.ProtectionSnapshot);
        Assert.True(File.Exists(Path.Combine(backupDirectory, "quicknotes-20261028-093000-000-manual.zip")));
    }
}
