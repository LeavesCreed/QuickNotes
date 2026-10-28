using QuickNotes.Application;
using Xunit;

namespace QuickNotes.UnitTests;

public sealed class RestoreCoordinatorTests
{
    [Fact]
    public void Constructor_NullDependencies_Throw()
    {
        var extractor = new FakeBackupPackageExtractor();
        var store = new FakeRestoreStore();
        var backups = CreateProtectionCoordinator();
        var drafts = new FakeRestoreDraftStore();
        var disk = new FakeDiskSpaceProvider();
        var gate = new DatabaseWriteGate();

        Assert.Throws<ArgumentNullException>(() => new RestoreCoordinator(
            null!, store, backups, drafts, disk, gate));
        Assert.Throws<ArgumentNullException>(() => new RestoreCoordinator(
            extractor, null!, backups, drafts, disk, gate));
        Assert.Throws<ArgumentNullException>(() => new RestoreCoordinator(
            extractor, store, null!, drafts, disk, gate));
        Assert.Throws<ArgumentNullException>(() => new RestoreCoordinator(
            extractor, store, backups, null!, disk, gate));
        Assert.Throws<ArgumentNullException>(() => new RestoreCoordinator(
            extractor, store, backups, drafts, null!, gate));
        Assert.Throws<ArgumentNullException>(() => new RestoreCoordinator(
            extractor, store, backups, drafts, disk, null!));
        Assert.Throws<ArgumentException>(() => new RestoreCoordinator(
            extractor,
            store,
            backups,
            drafts,
            disk,
            NoopDatabaseWriteGate.Instance));
    }

    [Fact]
    public async Task Preflight_ValidRequest_CanProceedWithManifestAndCounts()
    {
        using var context = new RestoreTestContext();

        var result = await context.Coordinator.PreflightAsync(context.Request);

        Assert.True(result.CanProceed);
        Assert.NotNull(result.Manifest);
        Assert.Equal(context.Store.StagedInfo.Counts, result.BackupCounts);
        Assert.Equal(context.Store.CurrentInfo.Counts, result.CurrentCounts);
        Assert.Equal(0, result.PendingDraftCount);
        Assert.True(result.RequiredBytes > 0);
        Assert.True(result.AvailableBytes > result.RequiredBytes);
        Assert.Null(result.Error);
        Assert.Equal(1, context.Extractor.Calls);
        Assert.Equal(1, context.Drafts.ListCalls);
    }

    [Fact]
    public async Task Preflight_PackageInvalid_ReturnsPackageInvalid()
    {
        using var context = new RestoreTestContext();
        context.Extractor.Handler = (_, _, _) => throw new BackupException(
            "extract",
            BackupErrorCodes.PackageVerificationFailed,
            "invalid package");

        var result = await context.Coordinator.PreflightAsync(context.Request);

        Assert.False(result.CanProceed);
        Assert.Equal(RestoreErrorCode.PackageInvalid, result.Error!.Code);
    }

    [Fact]
    public async Task Preflight_SchemaIncompatible_ReturnsSchemaIncompatible()
    {
        using var context = new RestoreTestContext();
        context.Extractor.Handler = (_, _, _) => Task.FromResult(
            BackupTestData.Manifest() with { SchemaVersion = 3 });

        var result = await context.Coordinator.PreflightAsync(context.Request);

        Assert.False(result.CanProceed);
        Assert.Equal(RestoreErrorCode.SchemaIncompatible, result.Error!.Code);
    }

    [Fact]
    public async Task Preflight_PendingDrafts_BlocksUnlessAllowed()
    {
        using var context = new RestoreTestContext();
        var draft = TestData.NewDraft(Guid.NewGuid(), TestData.NewNote());
        context.Drafts.Drafts.Add(draft);

        var blocked = await context.Coordinator.PreflightAsync(context.Request);
        Assert.False(blocked.CanProceed);
        Assert.Equal(RestoreErrorCode.PendingDrafts, blocked.Error!.Code);
        Assert.Equal(1, blocked.PendingDraftCount);

        var allowed = await context.Coordinator.PreflightAsync(
            context.Request with { AllowPendingDrafts = true });
        Assert.True(allowed.CanProceed);
        Assert.Equal(1, allowed.PendingDraftCount);
    }

    [Fact]
    public async Task Preflight_InsufficientSpace_ReturnsInsufficientSpace()
    {
        using var context = new RestoreTestContext();
        context.DiskSpace.AvailableBytes = 0;

        var result = await context.Coordinator.PreflightAsync(context.Request);

        Assert.False(result.CanProceed);
        Assert.Equal(RestoreErrorCode.InsufficientSpace, result.Error!.Code);
    }

    [Fact]
    public async Task Preflight_CurrentDatabaseMissing_ReturnsCurrentUnavailable()
    {
        using var context = new RestoreTestContext();
        File.Delete(context.Request.CurrentDatabasePath);

        var result = await context.Coordinator.PreflightAsync(context.Request);

        Assert.False(result.CanProceed);
        Assert.Equal(RestoreErrorCode.CurrentDatabaseUnavailable, result.Error!.Code);
    }

    [Fact]
    public async Task Preflight_BackupDirectoryMissing_ReturnsDestinationUnavailable()
    {
        using var context = new RestoreTestContext();
        Directory.Delete(context.Request.BackupDirectory, recursive: true);

        var result = await context.Coordinator.PreflightAsync(context.Request);

        Assert.False(result.CanProceed);
        Assert.Equal(RestoreErrorCode.DestinationUnavailable, result.Error!.Code);
    }

    [Fact]
    public async Task Preflight_WalSidecar_ReturnsCurrentUnavailable()
    {
        using var context = new RestoreTestContext();
        await File.WriteAllTextAsync(context.Request.CurrentDatabasePath + "-wal", "wal");

        var result = await context.Coordinator.PreflightAsync(context.Request);

        Assert.False(result.CanProceed);
        Assert.Equal(RestoreErrorCode.CurrentDatabaseUnavailable, result.Error!.Code);
        Assert.Equal(RestoreErrorCodes.WalUnsupported, result.Error.Diagnostics);
    }

    [Fact]
    public async Task Preflight_DraftCheckFailure_ReturnsDraftCheckFailed()
    {
        using var context = new RestoreTestContext();
        context.Drafts.ListFailure = new DraftStoreException(
            "list",
            null,
            "draft_io",
            "draft list failed");

        var result = await context.Coordinator.PreflightAsync(context.Request);

        Assert.False(result.CanProceed);
        Assert.Equal(RestoreErrorCode.DraftCheckFailed, result.Error!.Code);
    }

    [Fact]
    public async Task Restore_Success_ProtectsSwitchesVerifiesAndReleasesGate()
    {
        using var context = new RestoreTestContext();
        var draft = TestData.NewDraft(Guid.NewGuid(), TestData.NewNote());
        context.Drafts.Drafts.Add(draft);

        var result = await context.Coordinator.RestoreAsync(
            context.Request with { AllowPendingDrafts = true });

        Assert.Equal(RestoreStatus.Succeeded, result.Status);
        Assert.Null(result.Error);
        Assert.Equal(context.Store.StagedInfo.Counts, result.RestoredCounts);
        Assert.NotNull(result.ProtectionSnapshotPath);
        Assert.True(File.Exists(context.Request.PackagePath));
        Assert.Equal(1, context.ProtectionBackups.Calls);
        Assert.Equal(1, context.Store.SwitchCalls);
        Assert.Equal(0, context.Store.RollbackCalls);
        Assert.Contains(draft.DraftId, context.Drafts.Drafts.Select(item => item.DraftId));
        Assert.Equal(0, context.Drafts.DeleteCalls);
        Assert.Empty(Directory.GetDirectories(
            Path.GetDirectoryName(context.Request.CurrentDatabasePath)!,
            ".quicknotes-restore-*"));
        await using (await context.Gate.EnterWriteAsync())
        {
        }
    }

    [Fact]
    public async Task Restore_ProtectionFailure_DoesNotSwitch()
    {
        using var context = new RestoreTestContext(
            protectionBackups: new FakeBackupCoordinator
            {
                Handler = (_, _) => Task.FromResult(new BackupResult
                {
                    Status = BackupStatus.StorageUnavailable,
                    Error = new BackupError(
                        BackupErrorCode.PackageWriteFailed,
                        "protection failed"),
                    DurationMilliseconds = 1
                })
            });

        var result = await context.Coordinator.RestoreAsync(context.Request);

        Assert.Equal(RestoreStatus.ProtectionFailed, result.Status);
        Assert.Equal(RestoreErrorCode.ProtectionSnapshotFailed, result.Error!.Code);
        Assert.Equal(0, context.Store.SwitchCalls);
        Assert.Equal(0, context.Store.RollbackCalls);
    }

    [Fact]
    public async Task Restore_ExtractionFailure_DoesNotSwitch()
    {
        using var context = new RestoreTestContext();
        var calls = 0;
        context.Extractor.Handler = (_, _, _) =>
        {
            calls++;
            return calls == 1
                ? Task.FromResult(BackupTestData.Manifest())
                : throw new BackupException(
                    "extract",
                    BackupErrorCodes.PackageVerificationFailed,
                    "staging failed");
        };

        var result = await context.Coordinator.RestoreAsync(context.Request);

        Assert.Equal(RestoreStatus.VerificationFailed, result.Status);
        Assert.Equal(RestoreErrorCode.PackageInvalid, result.Error!.Code);
        Assert.NotNull(result.ProtectionSnapshotPath);
        Assert.Equal(0, context.Store.SwitchCalls);
        Assert.Equal(0, context.Store.RollbackCalls);
    }

    [Fact]
    public async Task Restore_SwitchFailureWithOldDatabase_ReturnsSwitchFailedWithoutRollback()
    {
        using var context = new RestoreTestContext();
        context.Store.SwitchFailure = new RestoreException(
            "switch",
            RestoreErrorCodes.SwitchFailed,
            "switch failed");

        var result = await context.Coordinator.RestoreAsync(context.Request);

        Assert.Equal(RestoreStatus.SwitchFailed, result.Status);
        Assert.Equal(RestoreErrorCode.SwitchFailed, result.Error!.Code);
        Assert.False(result.RolledBack);
        Assert.Equal(1, context.Store.SwitchCalls);
        Assert.Equal(0, context.Store.RollbackCalls);
        Assert.False(context.Gate.IsBlocked);
    }

    [Fact]
    public async Task Restore_PostSwitchMismatch_RollsBackAndReportsFailure()
    {
        using var context = new RestoreTestContext();
        context.Store.MismatchAfterSwitch = true;

        var result = await context.Coordinator.RestoreAsync(context.Request);

        Assert.Equal(RestoreStatus.SwitchFailed, result.Status);
        Assert.Equal(RestoreErrorCode.PostRestoreVerificationFailed, result.Error!.Code);
        Assert.True(result.RolledBack);
        Assert.False(result.WritesBlocked);
        Assert.Equal(1, context.Store.SwitchCalls);
        Assert.Equal(1, context.Store.RollbackCalls);
        Assert.False(context.Gate.IsBlocked);
    }

    [Fact]
    public async Task Restore_RollbackFailure_BlocksWritesAndPreservesRecoveryPaths()
    {
        using var context = new RestoreTestContext();
        context.Store.SwitchFailure = new RestoreException(
            "switch",
            RestoreErrorCodes.SwitchFailed,
            "switch failed");
        context.Store.SwitchThrowsAfterSwitching = true;
        context.Store.MismatchAfterSwitch = true;
        context.Store.RollbackFailure = new RestoreException(
            "rollback",
            RestoreErrorCodes.RollbackFailed,
            "rollback failed");

        var result = await context.Coordinator.RestoreAsync(context.Request);

        Assert.Equal(RestoreStatus.RollbackFailed, result.Status);
        Assert.Equal(RestoreErrorCode.RollbackFailed, result.Error!.Code);
        Assert.True(result.WritesBlocked);
        Assert.True(context.Gate.IsBlocked);
        Assert.NotNull(result.RecoveryHint);
        Assert.NotNull(result.ProtectionSnapshotPath);
        Assert.Equal(1, context.Store.RollbackCalls);
    }

    [Fact]
    public async Task Restore_WhenGateBlocked_ReturnsWritesBlocked()
    {
        using var context = new RestoreTestContext();
        context.Gate.BlockWrites();

        var result = await context.Coordinator.RestoreAsync(context.Request);

        Assert.Equal(RestoreStatus.PreflightFailed, result.Status);
        Assert.Equal(RestoreErrorCode.WritesBlocked, result.Error!.Code);
        Assert.True(result.WritesBlocked);
        Assert.Equal(0, context.ProtectionBackups.Calls);
        Assert.Equal(0, context.Store.SwitchCalls);
    }

    [Fact]
    public async Task Restore_SwitchFailureWithMatchingCountsButDifferentContent_DoesNotReportSuccess()
    {
        using var context = new RestoreTestContext();
        context.Store.CurrentInfo = context.Store.StagedInfo;
        context.Store.SwitchFailure = new RestoreException(
            "switch",
            RestoreErrorCodes.SwitchFailed,
            "switch failed");

        var result = await context.Coordinator.RestoreAsync(context.Request);

        Assert.Equal(RestoreStatus.SwitchFailed, result.Status);
        Assert.NotEqual(RestoreStatus.Succeeded, result.Status);
        Assert.Equal(0, context.Store.RollbackCalls);
        Assert.False(context.Gate.IsBlocked);
    }

    [Fact]
    public async Task Restore_CancelledAfterStaging_ThrowsAndCleansStaging()
    {
        using var context = new RestoreTestContext();
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        context.Extractor.Handler = (_, _, _) =>
        {
            calls++;
            if (calls == 2)
            {
                cancellation.Cancel();
            }

            return Task.FromResult(BackupTestData.Manifest());
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => context.Coordinator.RestoreAsync(context.Request, cancellation.Token));

        Assert.Equal(0, context.Store.SwitchCalls);
        Assert.Equal(1, context.ProtectionBackups.Calls);
        Assert.Empty(Directory.GetDirectories(
            Path.GetDirectoryName(context.Request.CurrentDatabasePath)!,
            ".quicknotes-restore-*"));
    }

    [Fact]
    public async Task Restore_CancelledBeforeStart_ThrowsOperationCanceled()
    {
        using var context = new RestoreTestContext();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => context.Coordinator.RestoreAsync(context.Request, cancellation.Token));

        Assert.Equal(0, context.Extractor.Calls);
        Assert.Equal(0, context.ProtectionBackups.Calls);
        Assert.Equal(0, context.Store.SwitchCalls);
    }

    private static FakeBackupCoordinator CreateProtectionCoordinator() => new()
    {
        Handler = (request, _) => Task.FromResult(new BackupResult
        {
            Status = BackupStatus.Succeeded,
            PackagePath = Path.Combine(request.BackupDirectory, "protection.zip"),
            Manifest = BackupTestData.Manifest(
                BackupType.Manual,
                BackupTestData.UtcNow,
                BackupTestData.UtcNow.ToOffset(TimeSpan.FromHours(8)),
                2),
            DurationMilliseconds = 1
        })
    };

    private sealed class RestoreTestContext : IDisposable
    {
        private readonly UnitTemporaryDirectory _temporary = new();

        public RestoreTestContext(FakeBackupCoordinator? protectionBackups = null)
        {
            var packagePath = Path.Combine(_temporary.RootPath, "backup.zip");
            var currentPath = Path.Combine(_temporary.RootPath, "notes.db");
            var backupDirectory = Path.Combine(_temporary.RootPath, "backups");
            File.WriteAllText(packagePath, "package");
            File.WriteAllText(currentPath, "current");
            Directory.CreateDirectory(backupDirectory);
            Request = new RestoreRequest
            {
                PackagePath = packagePath,
                CurrentDatabasePath = currentPath,
                BackupDirectory = backupDirectory
            };
            Store.CurrentDatabasePath = currentPath;
            ProtectionBackups = protectionBackups ?? CreateProtectionCoordinator();
            Coordinator = new RestoreCoordinator(
                Extractor,
                Store,
                ProtectionBackups,
                Drafts,
                DiskSpace,
                Gate);
        }

        public FakeBackupPackageExtractor Extractor { get; } = new();
        public FakeRestoreStore Store { get; } = new();
        public FakeBackupCoordinator ProtectionBackups { get; }
        public FakeRestoreDraftStore Drafts { get; } = new();
        public FakeDiskSpaceProvider DiskSpace { get; } = new();
        public DatabaseWriteGate Gate { get; } = new();
        public RestoreRequest Request { get; }
        public RestoreCoordinator Coordinator { get; }

        public void Dispose() => _temporary.Dispose();
    }
}
