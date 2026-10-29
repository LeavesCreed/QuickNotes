using System.Globalization;
using QuickNotes.Application;

namespace QuickNotes.UnitTests;

internal sealed class FakeBackupChangeDetector : IBackupChangeDetector
{
    public Func<CancellationToken, Task<string>> Handler { get; set; } =
        static _ => Task.FromResult("fingerprint");

    public int Calls { get; private set; }

    public Task<string> GetFingerprintAsync(CancellationToken cancellationToken = default)
    {
        Calls++;
        return Handler(cancellationToken);
    }
}

internal sealed class FakeBackupCatalog : IBackupCatalog
{
    public Func<CancellationToken, Task<BackupCatalog>> SchedulingHandler { get; set; } =
        static _ => Task.FromResult(DailyBackupTestData.EmptyCatalog());

    public Func<CancellationToken, Task<BackupCatalog>> RetentionHandler { get; set; } =
        static _ => Task.FromResult(DailyBackupTestData.EmptyCatalog());

    public int SchedulingCalls { get; private set; }
    public int RetentionCalls { get; private set; }

    public Task<BackupCatalog> ReadForSchedulingAsync(
        string backupDirectory,
        CancellationToken cancellationToken = default)
    {
        SchedulingCalls++;
        return SchedulingHandler(cancellationToken);
    }

    public Task<BackupCatalog> ReadForRetentionAsync(
        string backupDirectory,
        CancellationToken cancellationToken = default)
    {
        RetentionCalls++;
        return RetentionHandler(cancellationToken);
    }
}

internal sealed class FakeBackupCoordinator : IBackupCoordinator
{
    public Func<BackupRequest, CancellationToken, Task<BackupResult>> Handler { get; set; } =
        static (request, _) => Task.FromResult(DailyBackupTestData.Success(request.BackupDirectory));

    public int Calls { get; private set; }
    public BackupRequest? LastRequest { get; private set; }

    public Task<BackupResult> CreateAsync(
        BackupRequest request,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        LastRequest = request;
        return Handler(request, cancellationToken);
    }
}

internal sealed class FakeBackupRetentionPolicy : IBackupRetentionPolicy
{
    public Func<IReadOnlyList<BackupCatalogEntry>, IReadOnlyCollection<string>, BackupRetentionPlan> Handler { get; set; } =
        static (entries, _) => new BackupRetentionPlan(
            entries,
            Array.Empty<BackupCatalogEntry>());

    public int Calls { get; private set; }
    public IReadOnlyList<BackupCatalogEntry>? LastEntries { get; private set; }
    public IReadOnlyCollection<string>? LastProtectedPaths { get; private set; }

    public BackupRetentionPlan CreatePlan(
        IReadOnlyList<BackupCatalogEntry> entries,
        IReadOnlyCollection<string> protectedPackagePaths)
    {
        Calls++;
        LastEntries = entries;
        LastProtectedPaths = protectedPackagePaths;
        return Handler(entries, protectedPackagePaths);
    }
}

internal sealed class FakeBackupRetentionStore : IBackupRetentionStore
{
    public Func<string, IReadOnlyList<BackupCatalogEntry>, CancellationToken, Task<BackupRetentionResult>> Handler { get; set; } =
        static (_, _, _) => Task.FromResult(new BackupRetentionResult(
            Array.Empty<string>(),
            Array.Empty<BackupRetentionWarning>()));

    public int Calls { get; private set; }
    public IReadOnlyList<BackupCatalogEntry>? LastPackages { get; private set; }

    public Task<BackupRetentionResult> DeleteAsync(
        string backupDirectory,
        IReadOnlyList<BackupCatalogEntry> packages,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        LastPackages = packages;
        return Handler(backupDirectory, packages, cancellationToken);
    }
}

internal sealed class FakeBackupStateStore : IBackupStateStore
{
    public BackupScheduleState? State { get; set; }
    public Exception? ReadFailure { get; set; }
    public Exception? WriteFailure { get; set; }
    public int ReadCalls { get; private set; }
    public int WriteCalls { get; private set; }
    public BackupScheduleState? LastWritten { get; private set; }

    public Task<BackupScheduleState?> ReadAsync(
        string backupDirectory,
        CancellationToken cancellationToken = default)
    {
        ReadCalls++;
        if (ReadFailure is not null)
        {
            throw ReadFailure;
        }

        return Task.FromResult(State);
    }

    public Task WriteAsync(
        string backupDirectory,
        BackupScheduleState state,
        CancellationToken cancellationToken = default)
    {
        WriteCalls++;
        if (WriteFailure is not null)
        {
            throw WriteFailure;
        }

        LastWritten = state;
        return Task.CompletedTask;
    }
}

internal static class DailyBackupTestData
{
    public static BackupCatalog EmptyCatalog() => new()
    {
        ValidEntries = Array.Empty<BackupCatalogEntry>(),
        Issues = Array.Empty<BackupCatalogIssue>()
    };

    public static BackupCatalog Catalog(params BackupCatalogEntry[] entries) => new()
    {
        ValidEntries = entries,
        Issues = Array.Empty<BackupCatalogIssue>()
    };

    public static BackupCatalogEntry DailyEntry(
        string path,
        DateOnly localDate,
        DateTimeOffset? createdAtUtc = null)
    {
        var utc = createdAtUtc ?? new DateTimeOffset(
            localDate.ToDateTime(new TimeOnly(9, 0)),
            TimeSpan.Zero);
        var local = utc.ToOffset(TimeSpan.FromHours(8));
        return new BackupCatalogEntry
        {
            PackagePath = path,
            Manifest = BackupTestData.Manifest(BackupType.Daily, utc, local, 1),
            LocalDate = localDate,
            IsoWeekYear = ISOWeek.GetYear(local.DateTime),
            IsoWeek = ISOWeek.GetWeekOfYear(local.DateTime)
        };
    }

    public static BackupResult Success(string backupDirectory) =>
        Success(backupDirectory, BackupTestData.Marker(1));

    public static BackupResult Success(
        string backupDirectory,
        DataChangeMarker? marker)
    {
        var manifest = BackupTestData.Manifest(
            BackupType.Daily,
            BackupTestData.UtcNow,
            BackupTestData.UtcNow.ToOffset(TimeSpan.FromHours(8)),
            2);
        return new BackupResult
        {
            Status = BackupStatus.Succeeded,
            PackagePath = Path.Combine(backupDirectory, "daily.zip"),
            Manifest = manifest,
            Snapshot = marker is null
                ? null
                : new SnapshotInfo(manifest.SchemaVersion, marker),
            DurationMilliseconds = 1
        };
    }
}
