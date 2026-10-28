using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using QuickNotes.Application;
using QuickNotes.Domain;
using QuickNotes.Infrastructure.Backup;
using QuickNotes.Infrastructure.Sqlite;
using Xunit;

namespace QuickNotes.IntegrationTests;

public sealed class ZipBackupPackageStoreTests
{
    private static readonly DateTimeOffset UtcNow =
        new(2026, 10, 28, 9, 30, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset LocalNow =
        new(2026, 10, 28, 17, 30, 0, TimeSpan.FromHours(8));

    [Fact]
    public async Task PublishAsync_CreatesVerifiedPackageWithManifestAndHash()
    {
        using var database = await TestDatabase.CreateAsync();
        var tag = new Tag { Id = Guid.NewGuid(), Name = "b3-package" };
        await database.Catalog.CreateTagAsync(tag);
        await database.Notes.CreateAsync(TestData.NewNote(), new[] { tag.Id });
        using var temporary = new TemporaryDirectory();
        var snapshotPath = Path.Combine(temporary.RootPath, "snapshot.db");
        await new SqliteSnapshotProvider(database.DatabasePath).CreateAsync(snapshotPath);
        var store = new ZipBackupPackageStore();
        var request = new BackupRequest { BackupDirectory = temporary.RootPath };

        var info = await store.PublishAsync(snapshotPath, 2, request, UtcNow, LocalNow);

        Assert.True(File.Exists(info.PackagePath));
        Assert.EndsWith(".zip", info.PackagePath, StringComparison.Ordinal);
        Assert.Equal(BackupManifest.CurrentFormatVersion, info.Manifest.FormatVersion);
        Assert.Equal(BackupType.Manual, info.Manifest.BackupType);
        Assert.Equal(UtcNow, info.Manifest.CreatedAtUtc);
        Assert.Equal(LocalNow, info.Manifest.CreatedAtLocal);
        Assert.Equal(2, info.Manifest.SchemaVersion);
        Assert.Equal(BackupManifest.DatabaseEntryName, info.Manifest.DatabaseFileName);
        Assert.True(info.Manifest.DatabaseSizeBytes > 0);
        Assert.Equal(64, info.Manifest.DatabaseSha256.Length);
        using (var archive = ZipFile.OpenRead(info.PackagePath))
        {
            Assert.Equal(2, archive.Entries.Count);
            Assert.NotNull(archive.GetEntry(BackupManifest.ManifestEntryName));
            Assert.NotNull(archive.GetEntry(BackupManifest.DatabaseEntryName));
        }

        var verified = await new BackupPackageVerifier()
            .VerifyAsync(info.PackagePath, temporary.RootPath);
        Assert.Equal(info.Manifest, verified);
        Assert.Empty(Directory.GetFiles(temporary.RootPath, ".quicknotes-*"));
    }

    [Fact]
    public async Task PublishAsync_DoesNotOverwriteExistingPackage()
    {
        using var database = await TestDatabase.CreateAsync();
        await database.Notes.CreateAsync(TestData.NewNote(), Array.Empty<Guid>());
        using var temporary = new TemporaryDirectory();
        var snapshotPath = Path.Combine(temporary.RootPath, "snapshot.db");
        await new SqliteSnapshotProvider(database.DatabasePath).CreateAsync(snapshotPath);
        var existingPath = Path.Combine(
            temporary.RootPath,
            "quicknotes-20261028-093000-000-manual.zip");
        await File.WriteAllTextAsync(existingPath, "existing package");
        var store = new ZipBackupPackageStore();

        var info = await store.PublishAsync(
            snapshotPath,
            2,
            new BackupRequest { BackupDirectory = temporary.RootPath },
            UtcNow,
            LocalNow);

        Assert.NotEqual(existingPath, info.PackagePath);
        Assert.Equal("existing package", await File.ReadAllTextAsync(existingPath));
        Assert.True(File.Exists(info.PackagePath));
        await new BackupPackageVerifier().VerifyAsync(info.PackagePath, temporary.RootPath);
    }

    [Fact]
    public async Task PublishAsync_WhenBackupDirectoryMissing_ThrowsDestinationUnavailable()
    {
        using var database = await TestDatabase.CreateAsync();
        using var temporary = new TemporaryDirectory();
        var snapshotPath = Path.Combine(temporary.RootPath, "snapshot.db");
        await new SqliteSnapshotProvider(database.DatabasePath).CreateAsync(snapshotPath);
        var store = new ZipBackupPackageStore();
        var request = new BackupRequest
        {
            BackupDirectory = Path.Combine(temporary.RootPath, "missing")
        };

        var exception = await Assert.ThrowsAsync<BackupException>(
            () => store.PublishAsync(snapshotPath, 2, request, UtcNow, LocalNow));

        Assert.Equal(BackupErrorCodes.DestinationUnavailable, exception.ErrorCode);
    }

    [Fact]
    public async Task PublishAsync_WhenSnapshotMissing_ThrowsPackageWriteFailed()
    {
        using var temporary = new TemporaryDirectory();
        var store = new ZipBackupPackageStore();
        var request = new BackupRequest { BackupDirectory = temporary.RootPath };

        var exception = await Assert.ThrowsAsync<BackupException>(
            () => store.PublishAsync(
                Path.Combine(temporary.RootPath, "missing.db"),
                2,
                request,
                UtcNow,
                LocalNow));

        Assert.Equal(BackupErrorCodes.PackageWriteFailed, exception.ErrorCode);
    }

    [Fact]
    public async Task PublishAsync_WhenCancelled_DoesNotPublish()
    {
        using var database = await TestDatabase.CreateAsync();
        using var temporary = new TemporaryDirectory();
        var snapshotPath = Path.Combine(temporary.RootPath, "snapshot.db");
        await new SqliteSnapshotProvider(database.DatabasePath).CreateAsync(snapshotPath);
        var store = new ZipBackupPackageStore();
        var request = new BackupRequest { BackupDirectory = temporary.RootPath };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.PublishAsync(snapshotPath, 2, request, UtcNow, LocalNow, cancellation.Token));

        Assert.Empty(Directory.GetFiles(temporary.RootPath, "quicknotes-*.zip"));
        Assert.Empty(Directory.GetFiles(temporary.RootPath, ".quicknotes-*"));
    }

    [Fact]
    public async Task Verifier_RejectsExtraEntry()
    {
        var (temporary, packagePath) = await CreateVerifiedPackageAsync();
        using (temporary)
        {
            AddEntry(packagePath, "extra.txt", Encoding.UTF8.GetBytes("unexpected"));

            var exception = await Assert.ThrowsAsync<BackupException>(
                () => new BackupPackageVerifier().VerifyAsync(packagePath, temporary.RootPath));

            Assert.Equal(BackupErrorCodes.PackageVerificationFailed, exception.ErrorCode);
        }
    }

    [Fact]
    public async Task Verifier_RejectsPathTraversalEntry()
    {
        var (temporary, packagePath) = await CreateVerifiedPackageAsync();
        using (temporary)
        {
            AddEntry(packagePath, "../evil.txt", Encoding.UTF8.GetBytes("unexpected"));

            var exception = await Assert.ThrowsAsync<BackupException>(
                () => new BackupPackageVerifier().VerifyAsync(packagePath, temporary.RootPath));

            Assert.Equal(BackupErrorCodes.PackageVerificationFailed, exception.ErrorCode);
        }
    }

    [Fact]
    public async Task Verifier_RejectsDuplicateEntries()
    {
        using var temporary = new TemporaryDirectory();
        var packagePath = Path.Combine(temporary.RootPath, "duplicate.zip");
        await using (var stream = new FileStream(
            packagePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            for (var index = 0; index < 2; index++)
            {
                var entry = archive.CreateEntry(BackupManifest.DatabaseEntryName);
                await using var entryStream = entry.Open();
                await entryStream.WriteAsync(new byte[] { 1, 2, 3 });
            }
        }

        var exception = await Assert.ThrowsAsync<BackupException>(
            () => new BackupPackageVerifier().VerifyAsync(packagePath, temporary.RootPath));

        Assert.Equal(BackupErrorCodes.PackageVerificationFailed, exception.ErrorCode);
    }

    [Fact]
    public async Task Verifier_RejectsDatabaseAboveConfiguredLimit()
    {
        using var database = await TestDatabase.CreateAsync();
        using var temporary = new TemporaryDirectory();
        var snapshotPath = Path.Combine(temporary.RootPath, "snapshot.db");
        await new SqliteSnapshotProvider(database.DatabasePath).CreateAsync(snapshotPath);
        var databaseBytes = await File.ReadAllBytesAsync(snapshotPath);
        Assert.True(databaseBytes.Length > 1024);
        var manifest = new BackupManifest
        {
            FormatVersion = BackupManifest.CurrentFormatVersion,
            BackupType = BackupType.Manual,
            CreatedAtUtc = UtcNow,
            CreatedAtLocal = LocalNow,
            SchemaVersion = 1,
            DatabaseFileName = BackupManifest.DatabaseEntryName,
            DatabaseSizeBytes = databaseBytes.Length,
            DatabaseSha256 = Convert.ToHexString(SHA256.HashData(databaseBytes)).ToLowerInvariant()
        };
        var packagePath = Path.Combine(temporary.RootPath, "too-large.zip");
        await CreatePackageAsync(packagePath, databaseBytes, manifest);
        var verifier = new BackupPackageVerifier(maxDatabaseBytes: 1024);

        var exception = await Assert.ThrowsAsync<BackupException>(
            () => verifier.VerifyAsync(packagePath, temporary.RootPath));

        Assert.Equal(BackupErrorCodes.PackageVerificationFailed, exception.ErrorCode);
    }

    [Fact]
    public async Task PublishAsync_WhenVerificationFails_DoesNotPublishOrOverwrite()
    {
        using var database = await TestDatabase.CreateAsync();
        using var temporary = new TemporaryDirectory();
        var snapshotPath = Path.Combine(temporary.RootPath, "snapshot.db");
        await new SqliteSnapshotProvider(database.DatabasePath).CreateAsync(snapshotPath);
        var existingPath = Path.Combine(
            temporary.RootPath,
            "quicknotes-20261028-093000-000-manual.zip");
        await File.WriteAllTextAsync(existingPath, "existing package");
        var store = new ZipBackupPackageStore(
            new BackupPackageVerifier(maxDatabaseBytes: 1024));

        var exception = await Assert.ThrowsAsync<BackupException>(
            () => store.PublishAsync(
                snapshotPath,
                2,
                new BackupRequest { BackupDirectory = temporary.RootPath },
                UtcNow,
                LocalNow));

        Assert.Equal(BackupErrorCodes.PackageVerificationFailed, exception.ErrorCode);
        Assert.Equal("existing package", await File.ReadAllTextAsync(existingPath));
        var zipFiles = Directory.GetFiles(temporary.RootPath, "*.zip");
        Assert.Single(zipFiles);
        Assert.Equal(existingPath, zipFiles[0]);
        Assert.Empty(Directory.GetFiles(temporary.RootPath, ".quicknotes-*"));
    }

    [Fact]
    public async Task Verifier_RejectsHashMismatch()
    {
        var (temporary, packagePath) = await CreateVerifiedPackageAsync();
        using (temporary)
        {
            ReplaceEntry(packagePath, BackupManifest.DatabaseEntryName, new byte[] { 1, 2, 3 });

            var exception = await Assert.ThrowsAsync<BackupException>(
                () => new BackupPackageVerifier().VerifyAsync(packagePath, temporary.RootPath));

            Assert.Equal(BackupErrorCodes.PackageVerificationFailed, exception.ErrorCode);
        }
    }

    [Fact]
    public async Task Verifier_RejectsTruncatedZip()
    {
        var (temporary, packagePath) = await CreateVerifiedPackageAsync();
        using (temporary)
        {
            var bytes = await File.ReadAllBytesAsync(packagePath);
            var truncatedPath = Path.Combine(temporary.RootPath, "truncated.zip");
            await File.WriteAllBytesAsync(truncatedPath, bytes[..^30]);

            var exception = await Assert.ThrowsAsync<BackupException>(
                () => new BackupPackageVerifier().VerifyAsync(truncatedPath, temporary.RootPath));

            Assert.Equal(BackupErrorCodes.PackageVerificationFailed, exception.ErrorCode);
        }
    }

    [Fact]
    public async Task Verifier_RejectsSchemaVersionMismatch()
    {
        using var database = await TestDatabase.CreateAsync();
        using var temporary = new TemporaryDirectory();
        var snapshotPath = Path.Combine(temporary.RootPath, "snapshot.db");
        await new SqliteSnapshotProvider(database.DatabasePath).CreateAsync(snapshotPath);
        var databaseBytes = await File.ReadAllBytesAsync(snapshotPath);
        var manifest = new BackupManifest
        {
            FormatVersion = BackupManifest.CurrentFormatVersion,
            BackupType = BackupType.Manual,
            CreatedAtUtc = UtcNow,
            CreatedAtLocal = LocalNow,
            SchemaVersion = 99,
            DatabaseFileName = BackupManifest.DatabaseEntryName,
            DatabaseSizeBytes = databaseBytes.Length,
            DatabaseSha256 = Convert.ToHexString(SHA256.HashData(databaseBytes)).ToLowerInvariant()
        };
        var packagePath = Path.Combine(temporary.RootPath, "schema-mismatch.zip");
        await CreatePackageAsync(packagePath, databaseBytes, manifest);

        var exception = await Assert.ThrowsAsync<BackupException>(
            () => new BackupPackageVerifier().VerifyAsync(packagePath, temporary.RootPath));

        Assert.Equal(BackupErrorCodes.PackageVerificationFailed, exception.ErrorCode);
    }

    [Fact]
    public async Task Verifier_WhenPackageMissing_ThrowsPackageVerificationFailed()
    {
        using var temporary = new TemporaryDirectory();

        var exception = await Assert.ThrowsAsync<BackupException>(
            () => new BackupPackageVerifier().VerifyAsync(
                Path.Combine(temporary.RootPath, "missing.zip"),
                temporary.RootPath));

        Assert.Equal(BackupErrorCodes.PackageVerificationFailed, exception.ErrorCode);
    }

    private static async Task<(TemporaryDirectory Temporary, string PackagePath)> CreateVerifiedPackageAsync()
    {
        using var database = await TestDatabase.CreateAsync();
        var temporary = new TemporaryDirectory();
        var snapshotPath = Path.Combine(temporary.RootPath, "snapshot.db");
        await new SqliteSnapshotProvider(database.DatabasePath).CreateAsync(snapshotPath);
        var store = new ZipBackupPackageStore();
        var info = await store.PublishAsync(
            snapshotPath,
            2,
            new BackupRequest { BackupDirectory = temporary.RootPath },
            UtcNow,
            LocalNow);
        return (temporary, info.PackagePath);
    }

    private static void AddEntry(string packagePath, string name, byte[] content)
    {
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Update);
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(content);
    }

    private static void ReplaceEntry(string packagePath, string name, byte[] content)
    {
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Update);
        archive.GetEntry(name)?.Delete();
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(content);
    }

    private static async Task CreatePackageAsync(
        string packagePath,
        byte[] databaseBytes,
        BackupManifest manifest)
    {
        await using var stream = new FileStream(
            packagePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        var manifestEntry = archive.CreateEntry(BackupManifest.ManifestEntryName);
        await using (var entryStream = manifestEntry.Open())
        {
            await entryStream.WriteAsync(BackupManifestSerializer.Serialize(manifest));
        }

        var databaseEntry = archive.CreateEntry(BackupManifest.DatabaseEntryName);
        await using (var entryStream = databaseEntry.Open())
        {
            await entryStream.WriteAsync(databaseBytes);
        }
    }
}
