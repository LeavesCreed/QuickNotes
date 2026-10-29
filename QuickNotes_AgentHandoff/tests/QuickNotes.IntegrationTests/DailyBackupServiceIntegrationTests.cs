using QuickNotes.Application;
using QuickNotes.Infrastructure.Backup;
using QuickNotes.Infrastructure.Sqlite;
using Xunit;

namespace QuickNotes.IntegrationTests;

public sealed class DailyBackupServiceIntegrationTests
{
    private static readonly DateTimeOffset StartUtc =
        new(2026, 10, 28, 9, 30, 0, TimeSpan.Zero);

    private static readonly TimeZoneInfo LocalZone =
        TimeZoneInfo.CreateCustomTimeZone(
            "QuickNotes-B4-Test-Plus8",
            TimeSpan.FromHours(8),
            "QuickNotes-B4-Test-Plus8",
            "QuickNotes-B4-Test-Plus8");

    [Fact]
    public async Task RunCheck_DailyLifecycle_CreatesOnePerDayAndCapturesChanges()
    {
        using var database = await TestDatabase.CreateAsync();
        var note = TestData.NewNote();
        await database.Notes.CreateAsync(note, Array.Empty<Guid>());
        using var temporary = new TemporaryDirectory();
        var backupDirectory = CreateBackupDirectory(temporary);
        var time = new MutableTimeProvider(StartUtc, LocalZone);
        var service = CreateService(database.DatabasePath, backupDirectory, time);

        var first = await service.RunCheckAsync(backupDirectory);
        Assert.Equal(DailyBackupOutcome.BackedUp, first.Outcome);
        Assert.Single(DailyZips(backupDirectory));
        Assert.True(File.Exists(StatePath(backupDirectory)));

        var sameDay = await service.RunCheckAsync(backupDirectory);
        Assert.Equal(DailyBackupOutcome.AlreadyBackedUpToday, sameDay.Outcome);
        Assert.Single(DailyZips(backupDirectory));

        time.UtcNow = time.UtcNow.AddDays(1);
        var noChanges = await service.RunCheckAsync(backupDirectory);
        Assert.Equal(DailyBackupOutcome.NoChanges, noChanges.Outcome);
        Assert.Single(DailyZips(backupDirectory));

        await database.Notes.UpdateAsync(
            note with { Body = "changed", ModifiedAtUtc = note.ModifiedAtUtc.AddMinutes(1) },
            1,
            Array.Empty<Guid>());
        var changed = await service.RunCheckAsync(backupDirectory);
        Assert.Equal(DailyBackupOutcome.BackedUp, changed.Outcome);
        Assert.Equal(2, DailyZips(backupDirectory).Length);

        await database.Notes.UpdateAsync(
            (await database.Notes.GetAsync(note.Id))! with
            {
                Body = "same-day change",
                ModifiedAtUtc = note.ModifiedAtUtc.AddMinutes(2)
            },
            2,
            Array.Empty<Guid>());
        var sameDayAfterChange = await service.RunCheckAsync(backupDirectory);
        Assert.Equal(DailyBackupOutcome.AlreadyBackedUpToday, sameDayAfterChange.Outcome);
        Assert.Equal(2, DailyZips(backupDirectory).Length);

        time.UtcNow = time.UtcNow.AddDays(1);
        var nextDay = await service.RunCheckAsync(backupDirectory);
        Assert.Equal(DailyBackupOutcome.BackedUp, nextDay.Outcome);
        Assert.Equal(3, DailyZips(backupDirectory).Length);
        var catalog = await new FileBackupCatalog().ReadForRetentionAsync(backupDirectory);
        Assert.All(
            catalog.ValidEntries,
            entry => Assert.Equal(BackupType.Daily, entry.Manifest.BackupType));
    }

    [Fact]
    public async Task RunCheck_Retention_KeepsSevenDailyAndFourWeeklyAndManualBackups()
    {
        using var database = await TestDatabase.CreateAsync();
        await database.Notes.CreateAsync(TestData.NewNote(), Array.Empty<Guid>());
        using var temporary = new TemporaryDirectory();
        var backupDirectory = CreateBackupDirectory(temporary);
        var snapshotPath = Path.Combine(temporary.RootPath, "seed-snapshot.db");
        await new SqliteSnapshotProvider(database.DatabasePath).CreateAsync(snapshotPath);
        var seededDates = new[]
        {
            new DateOnly(2026, 9, 1),
            new DateOnly(2026, 9, 8),
            new DateOnly(2026, 9, 15),
            new DateOnly(2026, 9, 22),
            new DateOnly(2026, 9, 29),
            new DateOnly(2026, 9, 30),
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 10, 2),
            new DateOnly(2026, 10, 3),
            new DateOnly(2026, 10, 4),
            new DateOnly(2026, 10, 5),
            new DateOnly(2026, 10, 6)
        };
        var seededPaths = new Dictionary<DateOnly, string>();
        foreach (var date in seededDates)
        {
            var utc = new DateTimeOffset(
                date.ToDateTime(new TimeOnly(9, 0)),
                TimeSpan.Zero);
            seededPaths[date] = await PublishPackageAsync(
                snapshotPath,
                backupDirectory,
                BackupType.Daily,
                utc);
        }

        var manualPath = await PublishPackageAsync(
            snapshotPath,
            backupDirectory,
            BackupType.Manual,
            new DateTimeOffset(2026, 9, 15, 10, 0, 0, TimeSpan.Zero));
        var time = new MutableTimeProvider(StartUtc, LocalZone);
        var service = CreateService(database.DatabasePath, backupDirectory, time);

        var result = await service.RunCheckAsync(backupDirectory);

        Assert.Equal(DailyBackupOutcome.BackedUp, result.Outcome);
        Assert.True(File.Exists(result.Backup!.PackagePath));
        Assert.True(File.Exists(seededPaths[new DateOnly(2026, 9, 22)]));
        Assert.False(File.Exists(seededPaths[new DateOnly(2026, 9, 1)]));
        Assert.False(File.Exists(seededPaths[new DateOnly(2026, 9, 29)]));
        Assert.True(File.Exists(seededPaths[new DateOnly(2026, 10, 6)]));
        Assert.True(File.Exists(manualPath));

        var catalog = await new FileBackupCatalog().ReadForRetentionAsync(backupDirectory);
        var dailyEntries = catalog.ValidEntries
            .Where(entry => entry.Manifest.BackupType == BackupType.Daily)
            .ToList();
        Assert.Equal(8, dailyEntries.Count);
        var manual = Assert.Single(
            catalog.ValidEntries,
            entry => entry.Manifest.BackupType == BackupType.Manual);
        Assert.Equal(manualPath, manual.PackagePath);
        var plan = new BackupRetentionPolicy().CreatePlan(
            catalog.ValidEntries,
            Array.Empty<string>());
        Assert.Empty(plan.Delete);
        Assert.True(File.Exists(StatePath(backupDirectory)));
    }

    [Fact]
    public async Task RunCheck_WhenDirectoryMissing_ReturnsDestinationUnavailable()
    {
        using var database = await TestDatabase.CreateAsync();
        using var temporary = new TemporaryDirectory();
        var missing = Path.Combine(temporary.RootPath, "missing");
        var time = new MutableTimeProvider(StartUtc, LocalZone);
        var service = CreateService(database.DatabasePath, missing, time);

        var result = await service.RunCheckAsync(missing);

        Assert.Equal(DailyBackupOutcome.Failed, result.Outcome);
        Assert.Equal(BackupErrorCode.DestinationUnavailable, result.Error!.Code);
        Assert.True(result.Error.IsRetryable);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public async Task RunCheck_WhenSourceMissing_ReturnsSourceUnavailable()
    {
        using var temporary = new TemporaryDirectory();
        var backupDirectory = CreateBackupDirectory(temporary);
        var missingSource = Path.Combine(temporary.RootPath, "missing.db");
        var time = new MutableTimeProvider(StartUtc, LocalZone);
        var service = CreateService(missingSource, backupDirectory, time);

        var result = await service.RunCheckAsync(backupDirectory);

        Assert.Equal(DailyBackupOutcome.Failed, result.Outcome);
        Assert.Equal(BackupErrorCode.SourceUnavailable, result.Error!.Code);
        Assert.Empty(DailyZips(backupDirectory));
        Assert.False(File.Exists(StatePath(backupDirectory)));
    }

    [Fact]
    public async Task RunCheck_WhenBackupFails_DoesNotWriteStateOrRetain()
    {
        using var database = await TestDatabase.CreateAsync();
        await database.Notes.CreateAsync(TestData.NewNote(), Array.Empty<Guid>());
        using var temporary = new TemporaryDirectory();
        var backupDirectory = CreateBackupDirectory(temporary);
        var time = new MutableTimeProvider(StartUtc, LocalZone);
        var service = new DailyBackupService(
            new SqliteChangeDetector(database.DatabasePath),
            new FileBackupCatalog(),
            new FailingBackupCoordinator(),
            new BackupRetentionPolicy(),
            new FileBackupRetentionStore(),
            new FileBackupStateStore(),
            time);

        var result = await service.RunCheckAsync(backupDirectory);

        Assert.Equal(DailyBackupOutcome.Failed, result.Outcome);
        Assert.Equal(BackupErrorCode.PackageWriteFailed, result.Error!.Code);
        Assert.Empty(DailyZips(backupDirectory));
        Assert.False(File.Exists(StatePath(backupDirectory)));
    }

    [Fact]
    public async Task RunCheck_LegacyHeuristicStateFingerprint_ForcesBackup()
    {
        using var database = await TestDatabase.CreateAsync();
        await database.Notes.CreateAsync(TestData.NewNote(), Array.Empty<Guid>());
        using var temporary = new TemporaryDirectory();
        var backupDirectory = CreateBackupDirectory(temporary);
        var snapshotPath = Path.Combine(temporary.RootPath, "seed-snapshot.db");
        await new SqliteSnapshotProvider(database.DatabasePath).CreateAsync(snapshotPath);
        var priorAtUtc = StartUtc.AddDays(-1);
        await PublishPackageAsync(
            snapshotPath,
            backupDirectory,
            BackupType.Daily,
            priorAtUtc);
        var stateStore = new FileBackupStateStore();
        await stateStore.WriteAsync(backupDirectory, new BackupScheduleState
        {
            FormatVersion = BackupScheduleState.CurrentFormatVersion,
            LastSuccessfulFingerprint = "schema=1;notes=1;tags=0",
            LastSuccessfulDailyAtUtc = priorAtUtc,
            LastSuccessfulDailyLocalDate = DateOnly.FromDateTime(
                priorAtUtc.ToOffset(TimeSpan.FromHours(8)).DateTime)
        });
        var time = new MutableTimeProvider(StartUtc, LocalZone);
        var service = CreateService(database.DatabasePath, backupDirectory, time);

        var result = await service.RunCheckAsync(backupDirectory);

        Assert.Equal(DailyBackupOutcome.BackedUp, result.Outcome);
        var state = await stateStore.ReadAsync(backupDirectory);
        Assert.StartsWith(
            "schema=2;db=",
            state!.LastSuccessfulFingerprint,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunCheck_UnknownStateFormatVersion_TreatsStateAsMissingAndBacksUp()
    {
        using var database = await TestDatabase.CreateAsync();
        await database.Notes.CreateAsync(TestData.NewNote(), Array.Empty<Guid>());
        using var temporary = new TemporaryDirectory();
        var backupDirectory = CreateBackupDirectory(temporary);
        var snapshotPath = Path.Combine(temporary.RootPath, "seed-snapshot.db");
        await new SqliteSnapshotProvider(database.DatabasePath).CreateAsync(snapshotPath);
        var priorAtUtc = StartUtc.AddDays(-1);
        await PublishPackageAsync(
            snapshotPath,
            backupDirectory,
            BackupType.Daily,
            priorAtUtc);
        var currentFingerprint = await new SqliteChangeDetector(database.DatabasePath)
            .GetFingerprintAsync();
        await File.WriteAllTextAsync(
            StatePath(backupDirectory),
            $$"""
            {
              "formatVersion": 99,
              "lastSuccessfulFingerprint": "{{currentFingerprint}}",
              "lastSuccessfulDailyAtUtc": "{{priorAtUtc:O}}",
              "lastSuccessfulDailyLocalDate": "2026-10-27"
            }
            """);
        var time = new MutableTimeProvider(StartUtc, LocalZone);
        var service = CreateService(database.DatabasePath, backupDirectory, time);

        var result = await service.RunCheckAsync(backupDirectory);

        Assert.Equal(DailyBackupOutcome.BackedUp, result.Outcome);
        var state = await new FileBackupStateStore().ReadAsync(backupDirectory);
        Assert.NotNull(state);
        Assert.Equal(BackupScheduleState.CurrentFormatVersion, state!.FormatVersion);
    }

    [Fact]
    public async Task RunCheck_Cancelled_DoesNotCreatePackageOrState()
    {
        using var database = await TestDatabase.CreateAsync();
        await database.Notes.CreateAsync(TestData.NewNote(), Array.Empty<Guid>());
        using var temporary = new TemporaryDirectory();
        var backupDirectory = CreateBackupDirectory(temporary);
        var time = new MutableTimeProvider(StartUtc, LocalZone);
        var service = CreateService(database.DatabasePath, backupDirectory, time);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.RunCheckAsync(backupDirectory, cancellation.Token));

        Assert.Empty(DailyZips(backupDirectory));
        Assert.False(File.Exists(StatePath(backupDirectory)));
    }

    private static DailyBackupService CreateService(
        string sourceDatabasePath,
        string backupDirectory,
        MutableTimeProvider timeProvider)
    {
        var catalog = new FileBackupCatalog();
        var coordinator = new BackupCoordinator(
            new SqliteSnapshotProvider(sourceDatabasePath),
            new ZipBackupPackageStore(),
            timeProvider);
        return new DailyBackupService(
            new SqliteChangeDetector(sourceDatabasePath),
            catalog,
            coordinator,
            new BackupRetentionPolicy(),
            new FileBackupRetentionStore(),
            new FileBackupStateStore(),
            timeProvider);
    }

    private static string CreateBackupDirectory(TemporaryDirectory temporary)
    {
        var backupDirectory = Path.Combine(temporary.RootPath, "backups");
        Directory.CreateDirectory(backupDirectory);
        return backupDirectory;
    }

    private static async Task<string> PublishPackageAsync(
        string snapshotPath,
        string backupDirectory,
        BackupType type,
        DateTimeOffset createdAtUtc)
    {
        var local = createdAtUtc.ToOffset(TimeSpan.FromHours(8));
        var info = await new ZipBackupPackageStore().PublishAsync(
            snapshotPath,
            2,
            new BackupRequest
            {
                BackupDirectory = backupDirectory,
                Type = type
            },
            createdAtUtc,
            local);
        return info.PackagePath;
    }

    private static string[] DailyZips(string backupDirectory) =>
        Directory.GetFiles(backupDirectory, "quicknotes-*-daily.zip");

    private static string StatePath(string backupDirectory) =>
        Path.Combine(backupDirectory, FileBackupStateStore.StateFileName);

    private sealed class MutableTimeProvider : TimeProvider
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

    private sealed class FailingBackupCoordinator : IBackupCoordinator
    {
        public Task<BackupResult> CreateAsync(
            BackupRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new BackupResult
            {
                Status = BackupStatus.StorageUnavailable,
                Error = new BackupError(
                    BackupErrorCode.PackageWriteFailed,
                    "simulated backup failure"),
                DurationMilliseconds = 1
            });
    }
}
