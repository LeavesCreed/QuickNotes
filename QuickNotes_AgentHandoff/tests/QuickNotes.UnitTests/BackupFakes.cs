using QuickNotes.Application;

namespace QuickNotes.UnitTests;

internal sealed class FakeBackupSnapshotProvider : IBackupSnapshotProvider
{
    public Func<string, CancellationToken, Task<SnapshotInfo>> Handler { get; set; } =
        static (path, _) =>
        {
            File.WriteAllText(path, "snapshot");
            return Task.FromResult(new SnapshotInfo(2));
        };

    public int Calls { get; private set; }
    public string? LastDestinationPath { get; private set; }

    public Task<SnapshotInfo> CreateAsync(
        string destinationDatabasePath,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        LastDestinationPath = destinationDatabasePath;
        return Handler(destinationDatabasePath, cancellationToken);
    }
}

internal sealed class FakeBackupPackageStore : IBackupPackageStore
{
    public Func<string, long, BackupRequest, DateTimeOffset, DateTimeOffset, CancellationToken, Task<BackupPackageInfo>> Handler { get; set; } =
        static (_, schemaVersion, request, utc, local, _) =>
        {
            var manifest = BackupTestData.Manifest(request.Type, utc, local, schemaVersion);
            return Task.FromResult(new BackupPackageInfo(
                Path.Combine(request.BackupDirectory, "package.zip"),
                manifest));
        };

    public int Calls { get; private set; }
    public string? LastSnapshotPath { get; private set; }
    public long? LastSchemaVersion { get; private set; }
    public BackupRequest? LastRequest { get; private set; }
    public DateTimeOffset? LastCreatedAtUtc { get; private set; }
    public DateTimeOffset? LastCreatedAtLocal { get; private set; }

    public Task<BackupPackageInfo> PublishAsync(
        string snapshotDatabasePath,
        long schemaVersion,
        BackupRequest request,
        DateTimeOffset createdAtUtc,
        DateTimeOffset createdAtLocal,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        LastSnapshotPath = snapshotDatabasePath;
        LastSchemaVersion = schemaVersion;
        LastRequest = request;
        LastCreatedAtUtc = createdAtUtc;
        LastCreatedAtLocal = createdAtLocal;
        return Handler(
            snapshotDatabasePath,
            schemaVersion,
            request,
            createdAtUtc,
            createdAtLocal,
            cancellationToken);
    }
}

internal sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _utcNow;
    private readonly TimeZoneInfo _localTimeZone;

    public FixedTimeProvider(DateTimeOffset utcNow, TimeZoneInfo localTimeZone)
    {
        _utcNow = utcNow.ToUniversalTime();
        _localTimeZone = localTimeZone;
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;
    public override TimeZoneInfo LocalTimeZone => _localTimeZone;
}

internal static class BackupTestData
{
    public static readonly DateTimeOffset UtcNow =
        new(2026, 10, 28, 9, 30, 0, TimeSpan.Zero);

    public static readonly TimeZoneInfo LocalZone =
        TimeZoneInfo.CreateCustomTimeZone(
            "QuickNotes-Test-Plus8",
            TimeSpan.FromHours(8),
            "QuickNotes-Test-Plus8",
            "QuickNotes-Test-Plus8");

    public static BackupManifest Manifest(
        BackupType type = BackupType.Manual,
        DateTimeOffset? utc = null,
        DateTimeOffset? local = null,
        long schemaVersion = 2)
    {
        var utcNow = utc ?? UtcNow;
        return new BackupManifest
        {
            FormatVersion = BackupManifest.CurrentFormatVersion,
            BackupType = type,
            CreatedAtUtc = utcNow,
            CreatedAtLocal = local ?? utcNow.ToOffset(TimeSpan.FromHours(8)),
            SchemaVersion = schemaVersion,
            DatabaseFileName = BackupManifest.DatabaseEntryName,
            DatabaseSizeBytes = 4096,
            DatabaseSha256 = new string('a', 64)
        };
    }
}

internal sealed class UnitTemporaryDirectory : IDisposable
{
    public UnitTemporaryDirectory()
    {
        RootPath = Path.Combine(
            Path.GetTempPath(),
            "QuickNotes-B3-Unit",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    public void Dispose()
    {
        if (Directory.Exists(RootPath))
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }
}
