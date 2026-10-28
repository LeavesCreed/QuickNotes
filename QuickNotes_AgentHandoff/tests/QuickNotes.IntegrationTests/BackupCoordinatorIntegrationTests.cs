using System.IO.Compression;
using QuickNotes.Application;
using QuickNotes.Domain;
using QuickNotes.Infrastructure.Backup;
using QuickNotes.Infrastructure.Sqlite;
using Xunit;

namespace QuickNotes.IntegrationTests;

public sealed class BackupCoordinatorIntegrationTests
{
    private static readonly DateTimeOffset UtcNow =
        new(2026, 10, 28, 9, 30, 0, TimeSpan.Zero);

    private static readonly TimeZoneInfo LocalZone =
        TimeZoneInfo.CreateCustomTimeZone(
            "QuickNotes-Test-Plus8",
            TimeSpan.FromHours(8),
            "QuickNotes-Test-Plus8",
            "QuickNotes-Test-Plus8");

    [Fact]
    public async Task CreateAsync_EndToEnd_CreatesVerifiedPackageAndKeepsSourceWritable()
    {
        using var database = await TestDatabase.CreateAsync();
        var tag = new Tag { Id = Guid.NewGuid(), Name = "b3-e2e" };
        await database.Catalog.CreateTagAsync(tag);
        var note = TestData.NewNote();
        await database.Notes.CreateAsync(note, new[] { tag.Id });
        using var temporary = new TemporaryDirectory();
        var backupDirectory = CreateBackupDirectory(temporary);
        var coordinator = CreateCoordinator(database.DatabasePath);

        var result = await coordinator.CreateAsync(new BackupRequest
        {
            BackupDirectory = backupDirectory
        });

        Assert.Equal(BackupStatus.Succeeded, result.Status);
        Assert.NotNull(result.PackagePath);
        Assert.NotNull(result.Manifest);
        Assert.True(File.Exists(result.PackagePath));
        Assert.EndsWith(".zip", result.PackagePath, StringComparison.Ordinal);
        Assert.Equal(BackupType.Manual, result.Manifest!.BackupType);
        Assert.Equal(UtcNow, result.Manifest.CreatedAtUtc);
        Assert.Equal(TimeZoneInfo.ConvertTime(UtcNow, LocalZone), result.Manifest.CreatedAtLocal);
        Assert.Equal(1, result.Manifest.SchemaVersion);
        Assert.Equal(
            Path.Combine(backupDirectory, "quicknotes-20261028-093000-000-manual.zip"),
            result.PackagePath);
        using (var archive = ZipFile.OpenRead(result.PackagePath))
        {
            Assert.Equal(2, archive.Entries.Count);
            Assert.NotNull(archive.GetEntry(BackupManifest.ManifestEntryName));
            Assert.NotNull(archive.GetEntry(BackupManifest.DatabaseEntryName));
        }

        var verified = await new BackupPackageVerifier()
            .VerifyAsync(result.PackagePath, backupDirectory);
        Assert.Equal(result.Manifest, verified);
        Assert.Empty(Directory.GetDirectories(backupDirectory, ".quicknotes-tmp-*"));
        Assert.Empty(Directory.GetFiles(backupDirectory, ".quicknotes-*"));

        await database.Notes.UpdateAsync(
            note with { Body = "source writable after backup", ModifiedAtUtc = note.ModifiedAtUtc.AddMinutes(1) },
            1,
            new[] { tag.Id });
        Assert.Equal(
            "source writable after backup",
            (await database.Notes.GetAsync(note.Id))!.Body);
    }

    [Fact]
    public async Task CreateAsync_WithConcurrentWrites_SucceedsAndSourceRemainsWritable()
    {
        using var database = await TestDatabase.CreateAsync();
        var note = TestData.NewNote();
        await database.Notes.CreateAsync(note, Array.Empty<Guid>());
        using var temporary = new TemporaryDirectory();
        var backupDirectory = CreateBackupDirectory(temporary);
        using var writerCancellation = new CancellationTokenSource();
        var writer = Task.Run(async () =>
        {
            var counter = 0;
            while (!writerCancellation.IsCancellationRequested)
            {
                try
                {
                    var current = await database.Notes.GetAsync(note.Id);
                    if (current is null)
                    {
                        return;
                    }

                    await database.Notes.UpdateAsync(
                        current with
                        {
                            Body = $"concurrent-{counter++}",
                            ModifiedAtUtc = current.ModifiedAtUtc.AddSeconds(1)
                        },
                        current.Version,
                        Array.Empty<Guid>());
                }
                catch (NoteConcurrencyException)
                {
                }
                catch (StorageUnavailableException)
                {
                }

                await Task.Delay(5);
            }
        });
        var coordinator = CreateCoordinator(database.DatabasePath);
        try
        {
            await Task.Delay(20);
            var result = await coordinator.CreateAsync(new BackupRequest
            {
                BackupDirectory = backupDirectory
            });

            Assert.Equal(BackupStatus.Succeeded, result.Status);
            await new BackupPackageVerifier().VerifyAsync(result.PackagePath!, backupDirectory);
        }
        finally
        {
            writerCancellation.Cancel();
            await writer;
        }

        var latest = (await database.Notes.GetAsync(note.Id))!;
        var updated = await database.Notes.UpdateAsync(
            latest with
            {
                Body = "writable after concurrent backup",
                ModifiedAtUtc = latest.ModifiedAtUtc.AddMinutes(1)
            },
            latest.Version,
            Array.Empty<Guid>());
        Assert.Equal("writable after concurrent backup", updated.Body);
    }

    [Fact]
    public async Task CreateAsync_EmptyDatabase_Succeeds()
    {
        using var database = await TestDatabase.CreateAsync();
        using var temporary = new TemporaryDirectory();
        var backupDirectory = CreateBackupDirectory(temporary);
        var coordinator = CreateCoordinator(database.DatabasePath);

        var result = await coordinator.CreateAsync(new BackupRequest
        {
            BackupDirectory = backupDirectory
        });

        Assert.Equal(BackupStatus.Succeeded, result.Status);
        Assert.NotNull(result.Manifest);
        Assert.Equal(1, result.Manifest!.SchemaVersion);
        Assert.True(result.Manifest.DatabaseSizeBytes > 0);
        await new BackupPackageVerifier().VerifyAsync(result.PackagePath!, backupDirectory);
    }

    [Fact]
    public async Task CreateAsync_WhenSnapshotFails_CleansUpAndLeavesNoPackage()
    {
        using var database = await TestDatabase.CreateAsync();
        var note = TestData.NewNote();
        await database.Notes.CreateAsync(note, Array.Empty<Guid>());
        using var temporary = new TemporaryDirectory();
        var backupDirectory = CreateBackupDirectory(temporary);
        var coordinator = new BackupCoordinator(
            new FailingSnapshotProvider(
                new BackupException(
                    "snapshot",
                    BackupErrorCodes.SnapshotFailed,
                    "simulated snapshot failure")),
            new ZipBackupPackageStore(),
            new FixedBackupTimeProvider());

        var result = await coordinator.CreateAsync(new BackupRequest
        {
            BackupDirectory = backupDirectory
        });

        Assert.Equal(BackupStatus.SnapshotFailed, result.Status);
        Assert.Equal(BackupErrorCode.SnapshotFailed, result.Error!.Code);
        Assert.Empty(Directory.GetFiles(backupDirectory, "*.zip"));
        Assert.Empty(Directory.GetDirectories(backupDirectory, ".quicknotes-tmp-*"));
        Assert.NotNull(await database.Notes.GetAsync(note.Id));
    }

    [Fact]
    public async Task CreateAsync_WhenPackageFails_CleansUpAndLeavesNoPackage()
    {
        using var database = await TestDatabase.CreateAsync();
        var note = TestData.NewNote();
        await database.Notes.CreateAsync(note, Array.Empty<Guid>());
        using var temporary = new TemporaryDirectory();
        var backupDirectory = CreateBackupDirectory(temporary);
        var coordinator = new BackupCoordinator(
            new SqliteSnapshotProvider(database.DatabasePath),
            new FailingPackageStore(
                new BackupException(
                    "package",
                    BackupErrorCodes.PackageWriteFailed,
                    "simulated package failure")),
            new FixedBackupTimeProvider());

        var result = await coordinator.CreateAsync(new BackupRequest
        {
            BackupDirectory = backupDirectory
        });

        Assert.Equal(BackupStatus.StorageUnavailable, result.Status);
        Assert.Equal(BackupErrorCode.PackageWriteFailed, result.Error!.Code);
        Assert.Empty(Directory.GetFiles(backupDirectory, "*.zip"));
        Assert.Empty(Directory.GetDirectories(backupDirectory, ".quicknotes-tmp-*"));
        Assert.NotNull(await database.Notes.GetAsync(note.Id));
    }

    [Fact]
    public async Task CreateAsync_CancelledBeforeStart_DoesNotCreatePackage()
    {
        using var database = await TestDatabase.CreateAsync();
        using var temporary = new TemporaryDirectory();
        var backupDirectory = CreateBackupDirectory(temporary);
        var coordinator = CreateCoordinator(database.DatabasePath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.CreateAsync(
                new BackupRequest { BackupDirectory = backupDirectory },
                cancellation.Token));

        Assert.Empty(Directory.GetFiles(backupDirectory, "*.zip"));
        Assert.Empty(Directory.GetDirectories(backupDirectory, ".quicknotes-tmp-*"));
    }

    private static string CreateBackupDirectory(TemporaryDirectory temporary)
    {
        var backupDirectory = Path.Combine(temporary.RootPath, "backups");
        Directory.CreateDirectory(backupDirectory);
        return backupDirectory;
    }

    private static BackupCoordinator CreateCoordinator(string sourceDatabasePath)
    {
        return new BackupCoordinator(
            new SqliteSnapshotProvider(sourceDatabasePath),
            new ZipBackupPackageStore(),
            new FixedBackupTimeProvider());
    }

    private sealed class FixedBackupTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => UtcNow;
        public override TimeZoneInfo LocalTimeZone => LocalZone;
    }

    private sealed class FailingSnapshotProvider : IBackupSnapshotProvider
    {
        private readonly BackupException _failure;

        public FailingSnapshotProvider(BackupException failure)
        {
            _failure = failure;
        }

        public Task<SnapshotInfo> CreateAsync(
            string destinationDatabasePath,
            CancellationToken cancellationToken = default)
        {
            File.WriteAllText(destinationDatabasePath, "partial snapshot");
            throw _failure;
        }
    }

    private sealed class FailingPackageStore : IBackupPackageStore
    {
        private readonly BackupException _failure;

        public FailingPackageStore(BackupException failure)
        {
            _failure = failure;
        }

        public Task<BackupPackageInfo> PublishAsync(
            string snapshotDatabasePath,
            long schemaVersion,
            BackupRequest request,
            DateTimeOffset createdAtUtc,
            DateTimeOffset createdAtLocal,
            CancellationToken cancellationToken = default)
        {
            throw _failure;
        }
    }
}
