using Microsoft.Extensions.Logging;
using QuickNotes.Application;
using Xunit;

namespace QuickNotes.UnitTests;

public sealed class DailyBackupServiceTests
{
    private static readonly DateOnly Today = new(2026, 10, 28);
    private static readonly DateOnly Yesterday = new(2026, 10, 27);

    [Fact]
    public void Constructor_NullDependencies_Throw()
    {
        var detector = new FakeBackupChangeDetector();
        var catalog = new FakeBackupCatalog();
        var backups = new FakeBackupCoordinator();
        var policy = new FakeBackupRetentionPolicy();
        var retention = new FakeBackupRetentionStore();
        var state = new FakeBackupStateStore();

        Assert.Throws<ArgumentNullException>(() => new DailyBackupService(
            null!, catalog, backups, policy, retention, state));
        Assert.Throws<ArgumentNullException>(() => new DailyBackupService(
            detector, null!, backups, policy, retention, state));
        Assert.Throws<ArgumentNullException>(() => new DailyBackupService(
            detector, catalog, null!, policy, retention, state));
        Assert.Throws<ArgumentNullException>(() => new DailyBackupService(
            detector, catalog, backups, null!, retention, state));
        Assert.Throws<ArgumentNullException>(() => new DailyBackupService(
            detector, catalog, backups, policy, null!, state));
        Assert.Throws<ArgumentNullException>(() => new DailyBackupService(
            detector, catalog, backups, policy, retention, null!));
    }

    [Fact]
    public async Task RunCheck_FirstRun_BacksUpWritesStateAndRetains()
    {
        using var temporary = new UnitTemporaryDirectory();
        var detector = new FakeBackupChangeDetector
        {
            Handler = _ => Task.FromResult("fp1")
        };
        var catalog = new FakeBackupCatalog();
        var backups = new FakeBackupCoordinator();
        var policy = new FakeBackupRetentionPolicy();
        var retention = new FakeBackupRetentionStore();
        var state = new FakeBackupStateStore();
        var service = CreateService(detector, catalog, backups, policy, retention, state);

        var result = await service.RunCheckAsync(temporary.RootPath);

        Assert.Equal(DailyBackupOutcome.BackedUp, result.Outcome);
        Assert.Null(result.Error);
        Assert.Equal(1, backups.Calls);
        Assert.Equal(BackupType.Daily, backups.LastRequest!.Type);
        Assert.Equal(temporary.RootPath, backups.LastRequest.BackupDirectory);
        Assert.Equal(1, state.WriteCalls);
        Assert.Equal("fp1", state.LastWritten!.LastSuccessfulFingerprint);
        Assert.Equal(BackupTestData.UtcNow, state.LastWritten.LastSuccessfulDailyAtUtc);
        Assert.Equal(Today, state.LastWritten.LastSuccessfulDailyLocalDate);
        Assert.Equal(1, policy.Calls);
        Assert.Contains(result.Backup!.PackagePath, policy.LastProtectedPaths!);
        Assert.Equal(1, retention.Calls);
        Assert.NotNull(result.Retention);
    }

    [Fact]
    public async Task RunCheck_AlreadyBackedUpToday_SkipsEverythingElse()
    {
        using var temporary = new UnitTemporaryDirectory();
        var existing = DailyBackupTestData.DailyEntry(
            Path.Combine(temporary.RootPath, "existing.zip"),
            Today);
        var detector = new FakeBackupChangeDetector();
        var catalog = new FakeBackupCatalog
        {
            SchedulingHandler = _ => Task.FromResult(
                DailyBackupTestData.Catalog(existing))
        };
        var backups = new FakeBackupCoordinator();
        var retention = new FakeBackupRetentionStore();
        var state = new FakeBackupStateStore();
        var service = CreateService(
            detector,
            catalog,
            backups,
            new FakeBackupRetentionPolicy(),
            retention,
            state);

        var result = await service.RunCheckAsync(temporary.RootPath);

        Assert.Equal(DailyBackupOutcome.AlreadyBackedUpToday, result.Outcome);
        Assert.Equal(existing.PackagePath, result.ExistingDailyPackagePath);
        Assert.Equal(0, detector.Calls);
        Assert.Equal(0, backups.Calls);
        Assert.Equal(0, state.ReadCalls);
        Assert.Equal(0, retention.Calls);
    }

    [Fact]
    public async Task RunCheck_NoChanges_SkipsBackup()
    {
        using var temporary = new UnitTemporaryDirectory();
        var prior = DailyBackupTestData.DailyEntry(
            Path.Combine(temporary.RootPath, "prior.zip"),
            Yesterday);
        var detector = new FakeBackupChangeDetector
        {
            Handler = _ => Task.FromResult("fp1")
        };
        var catalog = new FakeBackupCatalog
        {
            SchedulingHandler = _ => Task.FromResult(
                DailyBackupTestData.Catalog(prior))
        };
        var backups = new FakeBackupCoordinator();
        var retention = new FakeBackupRetentionStore();
        var state = new FakeBackupStateStore
        {
            State = NewState("fp1", prior.LocalDate, prior.Manifest.CreatedAtUtc)
        };
        var service = CreateService(
            detector,
            catalog,
            backups,
            new FakeBackupRetentionPolicy(),
            retention,
            state);

        var result = await service.RunCheckAsync(temporary.RootPath);

        Assert.Equal(DailyBackupOutcome.NoChanges, result.Outcome);
        Assert.Equal(0, backups.Calls);
        Assert.Equal(0, state.WriteCalls);
        Assert.Equal(0, retention.Calls);
    }

    [Fact]
    public async Task RunCheck_NoChangesButBaselineDailyMissing_BacksUp()
    {
        using var temporary = new UnitTemporaryDirectory();
        var detector = new FakeBackupChangeDetector
        {
            Handler = _ => Task.FromResult("fp1")
        };
        var catalog = new FakeBackupCatalog();
        var backups = new FakeBackupCoordinator();
        var state = new FakeBackupStateStore
        {
            State = NewState("fp1", Yesterday, BackupTestData.UtcNow.AddDays(-1))
        };
        var service = CreateService(
            detector,
            catalog,
            backups,
            new FakeBackupRetentionPolicy(),
            new FakeBackupRetentionStore(),
            state);

        var result = await service.RunCheckAsync(temporary.RootPath);

        Assert.Equal(DailyBackupOutcome.BackedUp, result.Outcome);
        Assert.Equal(1, backups.Calls);
        Assert.Equal(1, state.WriteCalls);
    }

    [Fact]
    public async Task RunCheck_FingerprintChanges_BacksUpAndWritesNewState()
    {
        using var temporary = new UnitTemporaryDirectory();
        var prior = DailyBackupTestData.DailyEntry(
            Path.Combine(temporary.RootPath, "prior.zip"),
            Yesterday);
        var detector = new FakeBackupChangeDetector
        {
            Handler = _ => Task.FromResult("fp2")
        };
        var catalog = new FakeBackupCatalog
        {
            SchedulingHandler = _ => Task.FromResult(
                DailyBackupTestData.Catalog(prior))
        };
        var backups = new FakeBackupCoordinator();
        var state = new FakeBackupStateStore
        {
            State = NewState("fp1", prior.LocalDate, prior.Manifest.CreatedAtUtc)
        };
        var service = CreateService(
            detector,
            catalog,
            backups,
            new FakeBackupRetentionPolicy(),
            new FakeBackupRetentionStore(),
            state);

        var result = await service.RunCheckAsync(temporary.RootPath);

        Assert.Equal(DailyBackupOutcome.BackedUp, result.Outcome);
        Assert.Equal(1, backups.Calls);
        Assert.Equal("fp2", state.LastWritten!.LastSuccessfulFingerprint);
    }

    [Fact]
    public async Task RunCheck_BackupFails_DoesNotWriteStateOrRetain()
    {
        using var temporary = new UnitTemporaryDirectory();
        var backups = new FakeBackupCoordinator
        {
            Handler = (_, _) => Task.FromResult(new BackupResult
            {
                Status = BackupStatus.StorageUnavailable,
                Error = new BackupError(
                    BackupErrorCode.PackageWriteFailed,
                    "package failed"),
                DurationMilliseconds = 1
            })
        };
        var retention = new FakeBackupRetentionStore();
        var state = new FakeBackupStateStore();
        var service = CreateService(
            new FakeBackupChangeDetector(),
            new FakeBackupCatalog(),
            backups,
            new FakeBackupRetentionPolicy(),
            retention,
            state);

        var result = await service.RunCheckAsync(temporary.RootPath);

        Assert.Equal(DailyBackupOutcome.Failed, result.Outcome);
        Assert.Equal(BackupErrorCode.PackageWriteFailed, result.Error!.Code);
        Assert.NotNull(result.Backup);
        Assert.Equal(0, state.WriteCalls);
        Assert.Equal(0, retention.Calls);
    }

    [Fact]
    public async Task RunCheck_StateReadFails_ReturnsFailed()
    {
        using var temporary = new UnitTemporaryDirectory();
        var backups = new FakeBackupCoordinator();
        var state = new FakeBackupStateStore
        {
            ReadFailure = new BackupException(
                "state",
                BackupErrorCodes.DestinationUnavailable,
                "state read failed")
        };
        var service = CreateService(
            new FakeBackupChangeDetector(),
            new FakeBackupCatalog(),
            backups,
            new FakeBackupRetentionPolicy(),
            new FakeBackupRetentionStore(),
            state);

        var result = await service.RunCheckAsync(temporary.RootPath);

        Assert.Equal(DailyBackupOutcome.Failed, result.Outcome);
        Assert.Equal(BackupErrorCode.DestinationUnavailable, result.Error!.Code);
        Assert.Equal(0, backups.Calls);
    }

    [Fact]
    public async Task RunCheck_StateWriteFails_StillBackedUpAndRetains()
    {
        using var temporary = new UnitTemporaryDirectory();
        var retention = new FakeBackupRetentionStore();
        var state = new FakeBackupStateStore
        {
            WriteFailure = new BackupException(
                "state",
                BackupErrorCodes.DestinationUnavailable,
                "state failed")
        };
        var service = CreateService(
            new FakeBackupChangeDetector(),
            new FakeBackupCatalog(),
            new FakeBackupCoordinator(),
            new FakeBackupRetentionPolicy(),
            retention,
            state);

        var result = await service.RunCheckAsync(temporary.RootPath);

        Assert.Equal(DailyBackupOutcome.BackedUp, result.Outcome);
        Assert.NotNull(result.StateWarning);
        Assert.Contains("state_write_failed", result.StateWarning, StringComparison.Ordinal);
        Assert.Equal(1, retention.Calls);
    }

    [Fact]
    public async Task RunCheck_RetentionFails_StillBackedUpWithWarning()
    {
        using var temporary = new UnitTemporaryDirectory();
        var catalog = new FakeBackupCatalog
        {
            RetentionHandler = _ => throw new BackupException(
                "catalog",
                BackupErrorCodes.DestinationUnavailable,
                "retention unavailable")
        };
        var state = new FakeBackupStateStore();
        var service = CreateService(
            new FakeBackupChangeDetector(),
            catalog,
            new FakeBackupCoordinator(),
            new FakeBackupRetentionPolicy(),
            new FakeBackupRetentionStore(),
            state);

        var result = await service.RunCheckAsync(temporary.RootPath);

        Assert.Equal(DailyBackupOutcome.BackedUp, result.Outcome);
        Assert.NotNull(result.Retention);
        var warning = Assert.Single(result.Retention!.Warnings);
        Assert.Equal("retention_failed", warning.ErrorCode);
        Assert.Equal(1, state.WriteCalls);
    }

    [Fact]
    public async Task RunCheck_SchedulingCatalogFails_ReturnsFailed()
    {
        using var temporary = new UnitTemporaryDirectory();
        var catalog = new FakeBackupCatalog
        {
            SchedulingHandler = _ => throw new BackupException(
                "catalog",
                BackupErrorCodes.DestinationUnavailable,
                "directory unavailable")
        };
        var detector = new FakeBackupChangeDetector();
        var backups = new FakeBackupCoordinator();
        var service = CreateService(
            detector,
            catalog,
            backups,
            new FakeBackupRetentionPolicy(),
            new FakeBackupRetentionStore(),
            new FakeBackupStateStore());

        var result = await service.RunCheckAsync(temporary.RootPath);

        Assert.Equal(DailyBackupOutcome.Failed, result.Outcome);
        Assert.Equal(BackupErrorCode.DestinationUnavailable, result.Error!.Code);
        Assert.True(result.Error.IsRetryable);
        Assert.Equal(0, detector.Calls);
        Assert.Equal(0, backups.Calls);
    }

    [Fact]
    public async Task RunCheck_ChangeDetectorFails_ReturnsFailed()
    {
        using var temporary = new UnitTemporaryDirectory();
        var detector = new FakeBackupChangeDetector
        {
            Handler = _ => throw new BackupException(
                "change_detection",
                BackupErrorCodes.SourceUnavailable,
                "source unavailable")
        };
        var backups = new FakeBackupCoordinator();
        var service = CreateService(
            detector,
            new FakeBackupCatalog(),
            backups,
            new FakeBackupRetentionPolicy(),
            new FakeBackupRetentionStore(),
            new FakeBackupStateStore());

        var result = await service.RunCheckAsync(temporary.RootPath);

        Assert.Equal(DailyBackupOutcome.Failed, result.Outcome);
        Assert.Equal(BackupErrorCode.SourceUnavailable, result.Error!.Code);
        Assert.Equal(0, backups.Calls);
    }

    [Fact]
    public async Task RunCheck_CancelledBeforeStart_ThrowsOperationCanceled()
    {
        using var temporary = new UnitTemporaryDirectory();
        var detector = new FakeBackupChangeDetector();
        var catalog = new FakeBackupCatalog();
        var service = CreateService(
            detector,
            catalog,
            new FakeBackupCoordinator(),
            new FakeBackupRetentionPolicy(),
            new FakeBackupRetentionStore(),
            new FakeBackupStateStore());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.RunCheckAsync(temporary.RootPath, cancellation.Token));

        Assert.Equal(0, detector.Calls);
        Assert.Equal(0, catalog.SchedulingCalls);
    }

    [Fact]
    public async Task RunCheck_LogsDoNotContainFullBackupDirectory()
    {
        using var temporary = new UnitTemporaryDirectory();
        var backupDirectory = Path.Combine(temporary.RootPath, "SECRET-BACKUP-DIR");
        Directory.CreateDirectory(backupDirectory);
        var logger = new CapturingLogger<DailyBackupService>();
        var service = CreateService(
            new FakeBackupChangeDetector(),
            new FakeBackupCatalog(),
            new FakeBackupCoordinator(),
            new FakeBackupRetentionPolicy(),
            new FakeBackupRetentionStore(),
            new FakeBackupStateStore(),
            logger);

        await service.RunCheckAsync(backupDirectory);

        var messages = string.Join(Environment.NewLine, logger.Entries.Select(entry => entry.Message));
        Assert.DoesNotContain("SECRET-BACKUP-DIR", messages, StringComparison.Ordinal);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Information);
    }

    [Fact]
    public async Task RunPeriodic_RunsImmediatelyAndCanBeCancelled()
    {
        using var temporary = new UnitTemporaryDirectory();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new FakeBackupCatalog
        {
            SchedulingHandler = _ =>
            {
                started.TrySetResult();
                return Task.FromResult(DailyBackupTestData.EmptyCatalog());
            }
        };
        var service = CreateService(
            new FakeBackupChangeDetector(),
            catalog,
            new FakeBackupCoordinator(),
            new FakeBackupRetentionPolicy(),
            new FakeBackupRetentionStore(),
            new FakeBackupStateStore());
        using var cancellation = new CancellationTokenSource();

        var loop = service.RunPeriodicAsync(
            temporary.RootPath,
            TimeSpan.FromHours(1),
            cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
        Assert.True(catalog.SchedulingCalls >= 1);
    }

    [Fact]
    public async Task RunPeriodic_WhenCheckThrows_ContinuesUntilCancelled()
    {
        using var temporary = new UnitTemporaryDirectory();
        var calls = 0;
        var secondCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new FakeBackupCatalog
        {
            SchedulingHandler = _ =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    throw new InvalidOperationException("transient failure");
                }

                secondCall.TrySetResult();
                return Task.FromResult(DailyBackupTestData.EmptyCatalog());
            }
        };
        var service = CreateService(
            new FakeBackupChangeDetector(),
            catalog,
            new FakeBackupCoordinator(),
            new FakeBackupRetentionPolicy(),
            new FakeBackupRetentionStore(),
            new FakeBackupStateStore());
        using var cancellation = new CancellationTokenSource();

        var loop = service.RunPeriodicAsync(
            temporary.RootPath,
            TimeSpan.FromMilliseconds(25),
            cancellation.Token);
        await secondCall.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        var completed = await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(loop, completed);
        try
        {
            await loop;
        }
        catch (OperationCanceledException)
        {
        }

        Assert.True(calls >= 2);
    }

    [Fact]
    public async Task RunPeriodic_InvalidInterval_Throws()
    {
        using var temporary = new UnitTemporaryDirectory();
        var service = CreateService(
            new FakeBackupChangeDetector(),
            new FakeBackupCatalog(),
            new FakeBackupCoordinator(),
            new FakeBackupRetentionPolicy(),
            new FakeBackupRetentionStore(),
            new FakeBackupStateStore());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.RunPeriodicAsync(temporary.RootPath, TimeSpan.Zero));
    }

    private static DailyBackupService CreateService(
        FakeBackupChangeDetector detector,
        FakeBackupCatalog catalog,
        FakeBackupCoordinator backups,
        FakeBackupRetentionPolicy policy,
        FakeBackupRetentionStore retention,
        FakeBackupStateStore state,
        ILogger<DailyBackupService>? logger = null)
    {
        return new DailyBackupService(
            detector,
            catalog,
            backups,
            policy,
            retention,
            state,
            new FixedTimeProvider(BackupTestData.UtcNow, BackupTestData.LocalZone),
            logger);
    }

    private static BackupScheduleState NewState(
        string fingerprint,
        DateOnly localDate,
        DateTimeOffset dailyAtUtc) => new()
    {
        FormatVersion = BackupScheduleState.CurrentFormatVersion,
        LastSuccessfulFingerprint = fingerprint,
        LastSuccessfulDailyAtUtc = dailyAtUtc,
        LastSuccessfulDailyLocalDate = localDate
    };
}
