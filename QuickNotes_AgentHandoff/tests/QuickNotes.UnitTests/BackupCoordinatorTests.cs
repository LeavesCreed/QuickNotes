using Microsoft.Extensions.Logging;
using QuickNotes.Application;
using Xunit;

namespace QuickNotes.UnitTests;

public sealed class BackupCoordinatorTests
{
    [Fact]
    public void Constructor_NullDependencies_Throw()
    {
        Assert.Throws<ArgumentNullException>(
            () => new BackupCoordinator(null!, new FakeBackupPackageStore()));
        Assert.Throws<ArgumentNullException>(
            () => new BackupCoordinator(new FakeBackupSnapshotProvider(), null!));
    }

    [Fact]
    public async Task CreateAsync_Success_OrchestratesSnapshotAndPackageAndCleansWorkingDirectory()
    {
        using var temporary = new UnitTemporaryDirectory();
        var snapshots = new FakeBackupSnapshotProvider();
        var packages = new FakeBackupPackageStore();
        var coordinator = CreateCoordinator(snapshots, packages);
        var request = new BackupRequest { BackupDirectory = temporary.RootPath };

        var result = await coordinator.CreateAsync(request);

        Assert.Equal(BackupStatus.Succeeded, result.Status);
        Assert.NotNull(result.PackagePath);
        Assert.NotNull(result.Manifest);
        Assert.Equal(BackupType.Manual, result.Manifest!.BackupType);
        Assert.Null(result.Error);
        Assert.True(result.DurationMilliseconds >= 0);
        Assert.Equal(1, snapshots.Calls);
        Assert.Equal(1, packages.Calls);
        Assert.Equal(1, packages.LastSchemaVersion);
        Assert.Equal(BackupTestData.UtcNow, packages.LastCreatedAtUtc);
        Assert.Equal(
            TimeZoneInfo.ConvertTime(BackupTestData.UtcNow, BackupTestData.LocalZone),
            packages.LastCreatedAtLocal);
        Assert.NotNull(snapshots.LastDestinationPath);
        Assert.StartsWith(
            temporary.RootPath,
            snapshots.LastDestinationPath,
            StringComparison.OrdinalIgnoreCase);
        var workingDirectory = Path.GetDirectoryName(snapshots.LastDestinationPath)!;
        Assert.Contains(
            ".quicknotes-tmp-",
            Path.GetFileName(workingDirectory),
            StringComparison.Ordinal);
        Assert.False(Directory.Exists(workingDirectory));
    }

    [Fact]
    public async Task CreateAsync_EmptyDirectory_ReturnsValidationFailed()
    {
        var snapshots = new FakeBackupSnapshotProvider();
        var packages = new FakeBackupPackageStore();
        var coordinator = CreateCoordinator(snapshots, packages);

        var result = await coordinator.CreateAsync(new BackupRequest { BackupDirectory = " " });

        Assert.Equal(BackupStatus.ValidationFailed, result.Status);
        Assert.Equal(BackupErrorCode.ValidationFailed, result.Error!.Code);
        Assert.Equal("backup_directory_empty", result.Error.Diagnostics);
        Assert.Equal(0, result.DurationMilliseconds);
        Assert.Equal(0, snapshots.Calls);
        Assert.Equal(0, packages.Calls);
    }

    [Fact]
    public async Task CreateAsync_UnsupportedType_ReturnsValidationFailed()
    {
        using var temporary = new UnitTemporaryDirectory();
        var snapshots = new FakeBackupSnapshotProvider();
        var packages = new FakeBackupPackageStore();
        var coordinator = CreateCoordinator(snapshots, packages);

        var result = await coordinator.CreateAsync(new BackupRequest
        {
            BackupDirectory = temporary.RootPath,
            Type = BackupType.Automatic
        });

        Assert.Equal(BackupStatus.ValidationFailed, result.Status);
        Assert.Equal("unsupported_backup_type", result.Error!.Diagnostics);
        Assert.Equal(0, snapshots.Calls);
        Assert.Equal(0, packages.Calls);
    }

    [Fact]
    public async Task CreateAsync_DailyType_IsAccepted()
    {
        using var temporary = new UnitTemporaryDirectory();
        var snapshots = new FakeBackupSnapshotProvider();
        var packages = new FakeBackupPackageStore();
        var coordinator = CreateCoordinator(snapshots, packages);

        var result = await coordinator.CreateAsync(new BackupRequest
        {
            BackupDirectory = temporary.RootPath,
            Type = BackupType.Daily
        });

        Assert.Equal(BackupStatus.Succeeded, result.Status);
        Assert.Equal(BackupType.Daily, result.Manifest!.BackupType);
        Assert.Equal(BackupType.Daily, packages.LastRequest!.Type);
    }

    [Fact]
    public async Task CreateAsync_MissingDirectory_ReturnsDestinationUnavailable()
    {
        using var temporary = new UnitTemporaryDirectory();
        var snapshots = new FakeBackupSnapshotProvider();
        var packages = new FakeBackupPackageStore();
        var coordinator = CreateCoordinator(snapshots, packages);

        var result = await coordinator.CreateAsync(new BackupRequest
        {
            BackupDirectory = Path.Combine(temporary.RootPath, "missing")
        });

        Assert.Equal(BackupStatus.StorageUnavailable, result.Status);
        Assert.Equal(BackupErrorCode.DestinationUnavailable, result.Error!.Code);
        Assert.True(result.Error.IsRetryable);
        Assert.Equal(0, snapshots.Calls);
        Assert.Equal(0, packages.Calls);
    }

    [Fact]
    public async Task CreateAsync_MapsSnapshotFailures()
    {
        var cases = new[]
        {
            (
                new BackupException(
                    "snapshot",
                    BackupErrorCodes.SourceUnavailable,
                    "source busy",
                    isRetryable: true),
                BackupStatus.SnapshotFailed,
                BackupErrorCode.SourceUnavailable,
                true),
            (
                new BackupException(
                    "snapshot",
                    BackupErrorCodes.SnapshotFailed,
                    "snapshot failed"),
                BackupStatus.SnapshotFailed,
                BackupErrorCode.SnapshotFailed,
                false),
            (
                new BackupException(
                    "verify_snapshot",
                    BackupErrorCodes.SnapshotVerificationFailed,
                    "snapshot invalid"),
                BackupStatus.VerificationFailed,
                BackupErrorCode.SnapshotVerificationFailed,
                false)
        };

        foreach (var (failure, expectedStatus, expectedCode, expectedRetryable) in cases)
        {
            using var temporary = new UnitTemporaryDirectory();
            var snapshots = new FakeBackupSnapshotProvider
            {
                Handler = (_, _) => throw failure
            };
            var packages = new FakeBackupPackageStore();
            var coordinator = CreateCoordinator(snapshots, packages);

            var result = await coordinator.CreateAsync(
                new BackupRequest { BackupDirectory = temporary.RootPath });

            Assert.Equal(expectedStatus, result.Status);
            Assert.Equal(expectedCode, result.Error!.Code);
            Assert.Equal(expectedRetryable, result.Error.IsRetryable);
            Assert.Null(result.PackagePath);
            Assert.Equal(0, packages.Calls);
        }
    }

    [Fact]
    public async Task CreateAsync_MapsPackageFailures()
    {
        var cases = new[]
        {
            (
                new BackupException(
                    "package",
                    BackupErrorCodes.PackageWriteFailed,
                    "package write failed"),
                BackupStatus.StorageUnavailable,
                BackupErrorCode.PackageWriteFailed,
                false),
            (
                new BackupException(
                    "package",
                    BackupErrorCodes.DestinationUnavailable,
                    "destination unavailable"),
                BackupStatus.StorageUnavailable,
                BackupErrorCode.DestinationUnavailable,
                true),
            (
                new BackupException(
                    "verify_package",
                    BackupErrorCodes.PackageVerificationFailed,
                    "package invalid"),
                BackupStatus.VerificationFailed,
                BackupErrorCode.PackageVerificationFailed,
                false),
            (
                new BackupException(
                    "manifest",
                    BackupErrorCodes.ManifestInvalid,
                    "manifest invalid"),
                BackupStatus.VerificationFailed,
                BackupErrorCode.PackageVerificationFailed,
                false)
        };

        foreach (var (failure, expectedStatus, expectedCode, expectedRetryable) in cases)
        {
            using var temporary = new UnitTemporaryDirectory();
            var snapshots = new FakeBackupSnapshotProvider();
            var packages = new FakeBackupPackageStore
            {
                Handler = (_, _, _, _, _, _) => throw failure
            };
            var coordinator = CreateCoordinator(snapshots, packages);

            var result = await coordinator.CreateAsync(
                new BackupRequest { BackupDirectory = temporary.RootPath });

            Assert.Equal(expectedStatus, result.Status);
            Assert.Equal(expectedCode, result.Error!.Code);
            Assert.Equal(expectedRetryable, result.Error.IsRetryable);
            Assert.Null(result.PackagePath);
            Assert.Equal(1, snapshots.Calls);
        }
    }

    [Fact]
    public async Task CreateAsync_CancelledBeforeStart_ThrowsOperationCanceled()
    {
        using var temporary = new UnitTemporaryDirectory();
        var snapshots = new FakeBackupSnapshotProvider();
        var packages = new FakeBackupPackageStore();
        var coordinator = CreateCoordinator(snapshots, packages);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.CreateAsync(
                new BackupRequest { BackupDirectory = temporary.RootPath },
                cancellation.Token));

        Assert.Equal(0, snapshots.Calls);
        Assert.Equal(0, packages.Calls);
    }

    [Fact]
    public async Task CreateAsync_CancelledAfterSnapshot_ThrowsAndCleansWorkingDirectory()
    {
        using var temporary = new UnitTemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        var snapshots = new FakeBackupSnapshotProvider
        {
            Handler = (path, _) =>
            {
                File.WriteAllText(path, "snapshot");
                cancellation.Cancel();
                return Task.FromResult(new SnapshotInfo(1));
            }
        };
        var packages = new FakeBackupPackageStore();
        var coordinator = CreateCoordinator(snapshots, packages);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.CreateAsync(
                new BackupRequest { BackupDirectory = temporary.RootPath },
                cancellation.Token));

        Assert.Equal(0, packages.Calls);
        var workingDirectory = Path.GetDirectoryName(snapshots.LastDestinationPath!)!;
        Assert.False(Directory.Exists(workingDirectory));
    }

    [Fact]
    public async Task CreateAsync_UnexpectedException_IsRethrown()
    {
        using var temporary = new UnitTemporaryDirectory();
        var snapshots = new FakeBackupSnapshotProvider
        {
            Handler = (_, _) => throw new InvalidOperationException("programming error")
        };
        var packages = new FakeBackupPackageStore();
        var coordinator = CreateCoordinator(snapshots, packages);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.CreateAsync(
                new BackupRequest { BackupDirectory = temporary.RootPath }));

        Assert.Equal("programming error", exception.Message);
        var workingDirectory = Path.GetDirectoryName(snapshots.LastDestinationPath!)!;
        Assert.False(Directory.Exists(workingDirectory));
    }

    [Fact]
    public async Task CreateAsync_LogsDoNotContainFullBackupDirectory()
    {
        using var temporary = new UnitTemporaryDirectory();
        var backupDirectory = Path.Combine(temporary.RootPath, "SECRET-BACKUP-DIR");
        Directory.CreateDirectory(backupDirectory);
        var logger = new CapturingLogger<BackupCoordinator>();
        var snapshots = new FakeBackupSnapshotProvider();
        var packages = new FakeBackupPackageStore();
        var coordinator = new BackupCoordinator(
            snapshots,
            packages,
            new FixedTimeProvider(BackupTestData.UtcNow, BackupTestData.LocalZone),
            logger);

        await coordinator.CreateAsync(new BackupRequest { BackupDirectory = backupDirectory });

        var messages = string.Join(Environment.NewLine, logger.Entries.Select(entry => entry.Message));
        Assert.DoesNotContain("SECRET-BACKUP-DIR", messages, StringComparison.Ordinal);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Information);
    }

    private static BackupCoordinator CreateCoordinator(
        FakeBackupSnapshotProvider snapshots,
        FakeBackupPackageStore packages)
    {
        return new BackupCoordinator(
            snapshots,
            packages,
            new FixedTimeProvider(BackupTestData.UtcNow, BackupTestData.LocalZone));
    }
}
