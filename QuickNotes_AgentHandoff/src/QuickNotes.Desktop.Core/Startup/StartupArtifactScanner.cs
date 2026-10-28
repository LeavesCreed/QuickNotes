using QuickNotes.Desktop.Paths;

namespace QuickNotes.Desktop.Startup;

public sealed class StartupArtifactScanner
{
    private const string RestoreDirectoryPattern = ".quicknotes-restore-*";
    private const string RecoveryDirectoryPattern = ".quicknotes-recovery-*";
    private const string BackupTempDirectoryPattern = ".quicknotes-tmp-*";
    private const string PackageTempFilePattern = ".quicknotes-package-*.tmp";
    private const string VerifyTempFilePattern = ".quicknotes-verify-*";
    private const string ProbeTempFilePattern = ".quicknotes-probe-*";
    private const string QuarantineFilePattern = ".quicknotes-quarantine-*.db";
    private const string ManualBackupPattern = "quicknotes-*-manual.zip";

    private readonly DesktopPaths _paths;
    private readonly string? _backupDirectory;

    public StartupArtifactScanner(DesktopPaths paths, string? backupDirectory = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _backupDirectory = string.IsNullOrWhiteSpace(backupDirectory)
            ? null
            : Path.GetFullPath(backupDirectory);
    }

    public StartupArtifactInventory Scan()
    {
        var artifacts = new List<StartupArtifact>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddDirectories(artifacts, seen, _paths.DataRoot, RestoreDirectoryPattern, StartupArtifactKind.RestoreDirectory);
        AddDirectories(artifacts, seen, _paths.DataRoot, RecoveryDirectoryPattern, StartupArtifactKind.RecoveryDirectory);
        AddFileIfExists(artifacts, seen, _paths.DatabasePath + "-journal", StartupArtifactKind.Sidecar);
        AddFileIfExists(artifacts, seen, _paths.DatabasePath + "-wal", StartupArtifactKind.Sidecar);
        AddFileIfExists(artifacts, seen, _paths.DatabasePath + "-shm", StartupArtifactKind.Sidecar);
        AddFiles(artifacts, seen, _paths.DataRoot, PackageTempFilePattern, StartupArtifactKind.PackageTempFile);
        AddFiles(artifacts, seen, _paths.DataRoot, VerifyTempFilePattern, StartupArtifactKind.VerifyTempFile);
        AddFiles(artifacts, seen, _paths.DataRoot, ProbeTempFilePattern, StartupArtifactKind.VerifyTempFile);
        AddFiles(artifacts, seen, _paths.DataRoot, QuarantineFilePattern, StartupArtifactKind.QuarantinedDatabase);
        AddFiles(artifacts, seen, _paths.DraftDirectory, "*.json", StartupArtifactKind.DraftFile);

        if (_backupDirectory is not null)
        {
            AddDirectories(
                artifacts,
                seen,
                _backupDirectory,
                BackupTempDirectoryPattern,
                StartupArtifactKind.BackupTempDirectory);
            AddFiles(
                artifacts,
                seen,
                _backupDirectory,
                PackageTempFilePattern,
                StartupArtifactKind.PackageTempFile);
            AddFiles(
                artifacts,
                seen,
                _backupDirectory,
                VerifyTempFilePattern,
                StartupArtifactKind.VerifyTempFile);
            AddFiles(
                artifacts,
                seen,
                _backupDirectory,
                ProbeTempFilePattern,
                StartupArtifactKind.VerifyTempFile);
            AddFiles(
                artifacts,
                seen,
                _backupDirectory,
                ManualBackupPattern,
                StartupArtifactKind.ProtectionSnapshot);
        }

        artifacts.Sort((left, right) => string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase));
        return new StartupArtifactInventory(artifacts);
    }

    private static void AddDirectories(
        List<StartupArtifact> artifacts,
        HashSet<string> seen,
        string root,
        string pattern,
        StartupArtifactKind kind)
    {
        foreach (var path in SafeEnumerate(
                     () => Directory.EnumerateDirectories(root, pattern, SearchOption.TopDirectoryOnly)))
        {
            Add(artifacts, seen, path, kind, isDirectory: true);
        }
    }

    private static void AddFiles(
        List<StartupArtifact> artifacts,
        HashSet<string> seen,
        string root,
        string pattern,
        StartupArtifactKind kind)
    {
        foreach (var path in SafeEnumerate(
                     () => Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly)))
        {
            Add(artifacts, seen, path, kind, isDirectory: false);
        }
    }

    private static void AddFileIfExists(
        List<StartupArtifact> artifacts,
        HashSet<string> seen,
        string path,
        StartupArtifactKind kind)
    {
        if (File.Exists(path))
        {
            Add(artifacts, seen, path, kind, isDirectory: false);
        }
    }

    private static void Add(
        List<StartupArtifact> artifacts,
        HashSet<string> seen,
        string path,
        StartupArtifactKind kind,
        bool isDirectory)
    {
        var fullPath = Path.GetFullPath(path);
        if (!seen.Add(fullPath))
        {
            return;
        }

        artifacts.Add(new StartupArtifact(
            fullPath,
            kind,
            isDirectory,
            MayContainRecoveryData: true));
    }

    private static IEnumerable<string> SafeEnumerate(Func<IEnumerable<string>> enumerate)
    {
        try
        {
            return enumerate().ToArray();
        }
        catch (DirectoryNotFoundException)
        {
            return Array.Empty<string>();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }
}
