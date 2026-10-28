using System.IO.Compression;
using QuickNotes.Application;
using QuickNotes.Infrastructure.Backup;
using QuickNotes.Infrastructure.Sqlite;

namespace QuickNotes.IntegrationTests;

internal sealed class FaultingSnapshotProvider : IBackupSnapshotProvider
{
    public Task<SnapshotInfo> CreateAsync(
        string destinationDatabasePath,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(Path.GetFullPath(destinationDatabasePath))!);
        File.WriteAllText(destinationDatabasePath, "partial snapshot");
        throw new BackupException(
            "snapshot",
            BackupErrorCodes.SnapshotFailed,
            "simulated disk full during snapshot",
            new IOException("The disk is full."));
    }
}

internal sealed class FaultingPackageStore : IBackupPackageStore
{
    public Task<BackupPackageInfo> PublishAsync(
        string snapshotDatabasePath,
        long schemaVersion,
        BackupRequest request,
        DateTimeOffset createdAtUtc,
        DateTimeOffset createdAtLocal,
        CancellationToken cancellationToken = default) =>
        throw new BackupException(
            "package",
            BackupErrorCodes.PackageWriteFailed,
            "simulated disk full during package write",
            new IOException("The disk is full."));
}

internal sealed class CancelingPackageStore : IBackupPackageStore
{
    public Task<BackupPackageInfo> PublishAsync(
        string snapshotDatabasePath,
        long schemaVersion,
        BackupRequest request,
        DateTimeOffset createdAtUtc,
        DateTimeOffset createdAtLocal,
        CancellationToken cancellationToken = default)
    {
        var partialPath = Path.Combine(
            request.BackupDirectory,
            $".quicknotes-cancel-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(partialPath, "partial package");
        try
        {
            throw new OperationCanceledException("simulated cancellation");
        }
        finally
        {
            File.Delete(partialPath);
        }
    }
}

internal sealed class StagingFailingExtractor : IBackupPackageExtractor
{
    private readonly IBackupPackageExtractor _inner;
    private int _calls;

    public StagingFailingExtractor(IBackupPackageExtractor inner)
    {
        _inner = inner;
    }

    public Task<BackupManifest> ExtractAsync(
        string packagePath,
        string destinationDatabasePath,
        CancellationToken cancellationToken = default)
    {
        _calls++;
        if (_calls == 2)
        {
            throw new BackupException(
                "extract",
                BackupErrorCodes.DestinationUnavailable,
                "simulated disk full during restore staging",
                new IOException("The disk is full."));
        }

        return _inner.ExtractAsync(packagePath, destinationDatabasePath, cancellationToken);
    }
}

internal sealed class LockingRestoreStore : IRestoreStore
{
    private readonly IRestoreStore _inner;

    public LockingRestoreStore(IRestoreStore inner)
    {
        _inner = inner;
    }

    public Task<RestoreDatabaseInfo> InspectAsync(
        string databasePath,
        CancellationToken cancellationToken = default) =>
        _inner.InspectAsync(databasePath, cancellationToken);

    public Task<string?> TryGetDatabaseHashAsync(
        string databasePath,
        CancellationToken cancellationToken = default) =>
        _inner.TryGetDatabaseHashAsync(databasePath, cancellationToken);

    public Task<RestoreDatabaseInfo> PrepareCurrentAsync(
        string currentDatabasePath,
        CancellationToken cancellationToken = default) =>
        _inner.PrepareCurrentAsync(currentDatabasePath, cancellationToken);

    public async Task SwitchAsync(
        string stagedDatabasePath,
        string currentDatabasePath,
        string rollbackDatabasePath,
        CancellationToken cancellationToken = default)
    {
        using var lockStream = new FileStream(
            currentDatabasePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);
        await _inner.SwitchAsync(
            stagedDatabasePath,
            currentDatabasePath,
            rollbackDatabasePath,
            cancellationToken);
    }

    public Task RollbackAsync(
        string rollbackDatabasePath,
        string currentDatabasePath,
        string failedDatabasePath,
        CancellationToken cancellationToken = default) =>
        _inner.RollbackAsync(
            rollbackDatabasePath,
            currentDatabasePath,
            failedDatabasePath,
            cancellationToken);
}

internal sealed class RollbackFailingRestoreStore : IRestoreStore
{
    private readonly IRestoreStore _inner;
    private readonly string _currentDatabasePath;
    private bool _switched;

    public RollbackFailingRestoreStore(
        IRestoreStore inner,
        string currentDatabasePath)
    {
        _inner = inner;
        _currentDatabasePath = currentDatabasePath;
    }

    public async Task<RestoreDatabaseInfo> InspectAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        var info = await _inner.InspectAsync(databasePath, cancellationToken);
        if (_switched
            && string.Equals(databasePath, _currentDatabasePath, StringComparison.OrdinalIgnoreCase))
        {
            return new RestoreDatabaseInfo(
                info.SchemaVersion,
                new RestoreCounts(
                    info.Counts.ActiveNotes + 999,
                    info.Counts.ArchivedNotes,
                    info.Counts.TrashNotes,
                    info.Counts.Categories,
                    info.Counts.Tags,
                    info.Counts.NoteTags));
        }

        return info;
    }

    public Task<string?> TryGetDatabaseHashAsync(
        string databasePath,
        CancellationToken cancellationToken = default) =>
        _inner.TryGetDatabaseHashAsync(databasePath, cancellationToken);

    public Task<RestoreDatabaseInfo> PrepareCurrentAsync(
        string currentDatabasePath,
        CancellationToken cancellationToken = default) =>
        _inner.PrepareCurrentAsync(currentDatabasePath, cancellationToken);

    public async Task SwitchAsync(
        string stagedDatabasePath,
        string currentDatabasePath,
        string rollbackDatabasePath,
        CancellationToken cancellationToken = default)
    {
        await _inner.SwitchAsync(
            stagedDatabasePath,
            currentDatabasePath,
            rollbackDatabasePath,
            cancellationToken);
        _switched = true;
        throw new RestoreException(
            "switch",
            RestoreErrorCodes.SwitchFailed,
            "simulated switch ambiguity");
    }

    public Task RollbackAsync(
        string rollbackDatabasePath,
        string currentDatabasePath,
        string failedDatabasePath,
        CancellationToken cancellationToken = default) =>
        throw new RestoreException(
            "rollback",
            RestoreErrorCodes.RollbackFailed,
            "simulated rollback failure");
}

internal sealed class MutableTimeProvider : TimeProvider
{
    private readonly TimeZoneInfo _localTimeZone;

    public MutableTimeProvider(DateTimeOffset utcNow, TimeZoneInfo localTimeZone)
    {
        UtcNow = utcNow;
        _localTimeZone = localTimeZone;
    }

    public DateTimeOffset UtcNow { get; set; }

    public override DateTimeOffset GetUtcNow() => UtcNow;
    public override TimeZoneInfo LocalTimeZone => _localTimeZone;
}

internal static class FaultInjectionTestData
{
    public static void CorruptDatabaseEntry(string packagePath)
    {
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Update);
        var entry = archive.GetEntry(BackupManifest.DatabaseEntryName)!;
        entry.Delete();
        var replacement = archive.CreateEntry(BackupManifest.DatabaseEntryName);
        using var stream = replacement.Open();
        stream.Write(new byte[] { 1, 2, 3, 4 });
    }
}
