using QuickNotes.Application;
using QuickNotes.Domain;
using QuickNotes.Infrastructure.Backup;
using QuickNotes.Infrastructure.Drafts;
using QuickNotes.Infrastructure.Sqlite;
using Xunit;

namespace QuickNotes.IntegrationTests;

public sealed class RestoreIntegrationTests
{
    [Fact]
    public async Task Restore_EndToEnd_RestoresDataPreservesDraftsAndProtection()
    {
        using var current = await TestDatabase.CreateAsync();
        var currentTag = new Tag { Id = Guid.NewGuid(), Name = "current-tag" };
        await current.Catalog.CreateTagAsync(currentTag);
        var currentNote = TestData.NewNote() with
        {
            Title = "current title",
            Body = "current body"
        };
        await current.Notes.CreateAsync(currentNote, new[] { currentTag.Id });
        using var draftDirectory = new TemporaryDirectory();
        var draftStore = new FileNoteDraftStore(draftDirectory.DraftDirectory);
        var draft = new NoteDraft
        {
            DraftId = Guid.NewGuid(),
            NoteId = currentNote.Id,
            BaseVersion = 1,
            Title = "draft title",
            Body = "draft body",
            CategoryId = currentNote.CategoryId,
            TagIds = Array.Empty<Guid>(),
            CapturedAtUtc = currentNote.ModifiedAtUtc
        };
        await draftStore.SaveAsync(draft);
        using var backupDatabase = await TestDatabase.CreateAsync();
        var backupTag = new Tag { Id = Guid.NewGuid(), Name = "backup-tag" };
        await backupDatabase.Catalog.CreateTagAsync(backupTag);
        var backupNote = TestData.NewNote() with
        {
            Title = "backup title",
            Body = "backup body"
        };
        await backupDatabase.Notes.CreateAsync(backupNote, new[] { backupTag.Id });
        var archivedNote = TestData.NewNote();
        await backupDatabase.Notes.CreateAsync(archivedNote, Array.Empty<Guid>());
        await backupDatabase.Notes.SetLifecycleAsync(
            archivedNote.Id,
            NoteLifecycle.Archived,
            1,
            archivedNote.ModifiedAtUtc.AddMinutes(1));
        var trashedNote = TestData.NewNote();
        await backupDatabase.Notes.CreateAsync(trashedNote, Array.Empty<Guid>());
        await backupDatabase.Notes.SetLifecycleAsync(
            trashedNote.Id,
            NoteLifecycle.Trash,
            1,
            trashedNote.ModifiedAtUtc.AddMinutes(1));
        using var temporary = new TemporaryDirectory();
        var backupDirectory = Path.Combine(temporary.RootPath, "backups");
        Directory.CreateDirectory(backupDirectory);
        var snapshotPath = Path.Combine(temporary.RootPath, "snapshot.db");
        await new SqliteSnapshotProvider(backupDatabase.DatabasePath).CreateAsync(snapshotPath);
        var utcNow = DateTimeOffset.UtcNow;
        var localNow = TimeZoneInfo.ConvertTime(utcNow, TimeZoneInfo.Local);
        var package = await new ZipBackupPackageStore().PublishAsync(
            snapshotPath,
            1,
            new BackupRequest
            {
                BackupDirectory = backupDirectory,
                Type = BackupType.Manual
            },
            utcNow,
            localNow);
        var gate = new DatabaseWriteGate();
        var protectionCoordinator = new BackupCoordinator(
            new SqliteSnapshotProvider(current.DatabasePath),
            new ZipBackupPackageStore());
        var coordinator = new RestoreCoordinator(
            new BackupPackageVerifier(),
            new SqliteRestoreStore(),
            protectionCoordinator,
            draftStore,
            new DriveInfoDiskSpaceProvider(),
            gate);
        var request = new RestoreRequest
        {
            PackagePath = package.PackagePath,
            CurrentDatabasePath = current.DatabasePath,
            BackupDirectory = backupDirectory
        };

        var blocked = await coordinator.PreflightAsync(request);
        Assert.False(blocked.CanProceed);
        Assert.Equal(RestoreErrorCode.PendingDrafts, blocked.Error!.Code);
        Assert.Equal(1, blocked.PendingDraftCount);
        Assert.NotNull(await current.Notes.GetAsync(currentNote.Id));

        var allowedRequest = request with { AllowPendingDrafts = true };
        var preflight = await coordinator.PreflightAsync(allowedRequest);
        Assert.True(preflight.CanProceed);
        Assert.Equal(1, preflight.BackupCounts!.ActiveNotes);
        Assert.Equal(1, preflight.BackupCounts.ArchivedNotes);
        Assert.Equal(1, preflight.BackupCounts.TrashNotes);
        Assert.Equal(1, preflight.BackupCounts.Tags);
        Assert.Equal(1, preflight.BackupCounts.NoteTags);
        Assert.Equal(1, preflight.CurrentCounts!.ActiveNotes);
        Assert.Equal(1, preflight.CurrentCounts.Tags);
        Assert.Equal(1, preflight.CurrentCounts.NoteTags);
        Assert.Equal(1, preflight.Manifest!.SchemaVersion);

        var result = await coordinator.RestoreAsync(allowedRequest);

        Assert.Equal(RestoreStatus.Succeeded, result.Status);
        Assert.NotNull(result.ProtectionSnapshotPath);
        Assert.True(File.Exists(result.ProtectionSnapshotPath));
        Assert.Equal(1, result.RestoredCounts!.ActiveNotes);
        Assert.Equal(1, result.RestoredCounts.ArchivedNotes);
        Assert.Equal(1, result.RestoredCounts.TrashNotes);
        var restoredNotes = new SqliteNoteRepository(
            new SqliteConnectionFactory(current.DatabasePath));
        Assert.NotNull(await restoredNotes.GetAsync(backupNote.Id));
        Assert.Null(await restoredNotes.GetAsync(currentNote.Id));
        Assert.Equal("backup body", (await restoredNotes.GetAsync(backupNote.Id))!.Body);
        Assert.Equal(
            new[] { backupTag.Id },
            await restoredNotes.GetTagIdsAsync(backupNote.Id));
        Assert.Equal(
            NoteLifecycle.Archived,
            (await restoredNotes.GetAsync(archivedNote.Id))!.Lifecycle);
        Assert.Equal(
            NoteLifecycle.Trash,
            (await restoredNotes.GetAsync(trashedNote.Id))!.Lifecycle);
        Assert.NotNull(await draftStore.GetAsync(draft.DraftId));

        var protectionExtractPath = Path.Combine(temporary.RootPath, "protection.db");
        var protectionManifest = await new BackupPackageVerifier()
            .ExtractAsync(result.ProtectionSnapshotPath!, protectionExtractPath);
        Assert.Equal(BackupType.Manual, protectionManifest.BackupType);
        var protectionNotes = new SqliteNoteRepository(
            new SqliteConnectionFactory(protectionExtractPath));
        Assert.NotNull(await protectionNotes.GetAsync(currentNote.Id));
        Assert.Equal("current body", (await protectionNotes.GetAsync(currentNote.Id))!.Body);
        Assert.Equal(
            new[] { currentTag.Id },
            await protectionNotes.GetTagIdsAsync(currentNote.Id));

        var saveCoordinator = new NoteSaveCoordinator(
            restoredNotes,
            draftStore,
            new SaveRetryOptions
            {
                RetryDelays = new[] { TimeSpan.Zero, TimeSpan.Zero }
            },
            logger: null,
            writeGate: gate);
        var saved = await saveCoordinator.CreateAsync(TestData.NewNote(), Array.Empty<Guid>());
        Assert.Equal(SaveStatus.Saved, saved.Status);
    }

    [Fact]
    public async Task Restore_WithCorruptPackage_LeavesCurrentDatabaseUnchanged()
    {
        using var current = await TestDatabase.CreateAsync();
        var currentNote = TestData.NewNote() with { Body = "keep me" };
        await current.Notes.CreateAsync(currentNote, Array.Empty<Guid>());
        using var backupDatabase = await TestDatabase.CreateAsync();
        await backupDatabase.Notes.CreateAsync(TestData.NewNote(), Array.Empty<Guid>());
        using var temporary = new TemporaryDirectory();
        var backupDirectory = Path.Combine(temporary.RootPath, "backups");
        Directory.CreateDirectory(backupDirectory);
        var snapshotPath = Path.Combine(temporary.RootPath, "snapshot.db");
        await new SqliteSnapshotProvider(backupDatabase.DatabasePath).CreateAsync(snapshotPath);
        var utcNow = DateTimeOffset.UtcNow;
        var package = await new ZipBackupPackageStore().PublishAsync(
            snapshotPath,
            1,
            new BackupRequest
            {
                BackupDirectory = backupDirectory,
                Type = BackupType.Manual
            },
            utcNow,
            TimeZoneInfo.ConvertTime(utcNow, TimeZoneInfo.Local));
        FaultInjectionTestData.CorruptDatabaseEntry(package.PackagePath);
        var draftStore = new FileNoteDraftStore(Path.Combine(temporary.RootPath, "drafts"));
        var coordinator = CreateCoordinator(
            current.DatabasePath,
            backupDirectory,
            draftStore);

        var result = await coordinator.RestoreAsync(new RestoreRequest
        {
            PackagePath = package.PackagePath,
            CurrentDatabasePath = current.DatabasePath,
            BackupDirectory = backupDirectory,
            AllowPendingDrafts = true
        });

        Assert.Equal(RestoreStatus.PreflightFailed, result.Status);
        Assert.Equal(RestoreErrorCode.PackageInvalid, result.Error!.Code);
        Assert.Equal("keep me", (await current.Notes.GetAsync(currentNote.Id))!.Body);
    }

    [Fact]
    public async Task Restore_WithSchemaIncompatiblePackage_LeavesCurrentDatabaseUnchanged()
    {
        using var current = await TestDatabase.CreateAsync();
        var currentNote = TestData.NewNote();
        await current.Notes.CreateAsync(currentNote, Array.Empty<Guid>());
        using var backupDatabase = await TestDatabase.CreateAsync();
        await backupDatabase.Notes.CreateAsync(TestData.NewNote(), Array.Empty<Guid>());
        await ExecuteAsync(backupDatabase.Factory, "PRAGMA user_version=2;");
        using var temporary = new TemporaryDirectory();
        var backupDirectory = Path.Combine(temporary.RootPath, "backups");
        Directory.CreateDirectory(backupDirectory);
        var snapshotPath = Path.Combine(temporary.RootPath, "snapshot.db");
        await new SqliteSnapshotProvider(backupDatabase.DatabasePath).CreateAsync(snapshotPath);
        var utcNow = DateTimeOffset.UtcNow;
        var package = await new ZipBackupPackageStore().PublishAsync(
            snapshotPath,
            2,
            new BackupRequest
            {
                BackupDirectory = backupDirectory,
                Type = BackupType.Manual
            },
            utcNow,
            TimeZoneInfo.ConvertTime(utcNow, TimeZoneInfo.Local));
        var draftStore = new FileNoteDraftStore(Path.Combine(temporary.RootPath, "drafts"));
        var coordinator = CreateCoordinator(
            current.DatabasePath,
            backupDirectory,
            draftStore);

        var result = await coordinator.RestoreAsync(new RestoreRequest
        {
            PackagePath = package.PackagePath,
            CurrentDatabasePath = current.DatabasePath,
            BackupDirectory = backupDirectory,
            AllowPendingDrafts = true
        });

        Assert.Equal(RestoreStatus.PreflightFailed, result.Status);
        Assert.Equal(RestoreErrorCode.SchemaIncompatible, result.Error!.Code);
        Assert.NotNull(await current.Notes.GetAsync(currentNote.Id));
    }

    private static RestoreCoordinator CreateCoordinator(
        string currentDatabasePath,
        string backupDirectory,
        INoteDraftStore draftStore)
    {
        return new RestoreCoordinator(
            new BackupPackageVerifier(),
            new SqliteRestoreStore(),
            new BackupCoordinator(
                new SqliteSnapshotProvider(currentDatabasePath),
                new ZipBackupPackageStore()),
            draftStore,
            new DriveInfoDiskSpaceProvider(),
            new DatabaseWriteGate());
    }

    private static async Task ExecuteAsync(SqliteConnectionFactory factory, string sql)
    {
        await using var connection = await factory.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
