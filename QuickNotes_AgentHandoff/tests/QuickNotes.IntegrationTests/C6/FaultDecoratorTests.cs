using QuickNotes.Application;
using QuickNotes.C6.Harness;
using QuickNotes.Domain;
using QuickNotes.Infrastructure.Drafts;
using QuickNotes.Infrastructure.Sqlite;

namespace QuickNotes.IntegrationTests.C6;

/// <summary>
/// Task 2 decorator and scenario contracts. Every case here runs in a
/// disposable <c>%TEMP%\QuickNotes-C6</c> root and uses the production SQLite,
/// draft, and backup services; only the fault injection is a test composition.
/// </summary>
public sealed class FaultDecoratorTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task Save_DiskFullFake_ReturnsStorageUnavailableAndKeepsDraft()
    {
        var inner = new FaultingNoteSaveCoordinator(_ => throw new IOException("disk full"));

        var result = await inner.CreateAsync(TestData.NewNote(), Array.Empty<Guid>());

        Assert.Equal(SaveStatus.StorageUnavailable, result.Status);
        Assert.NotEqual(SaveStatus.Saved, result.Status);
        Assert.Equal(DraftDisposition.NotProvided, result.DraftDisposition);
    }

    [Fact]
    public async Task Save_DiskFull_ReturnsStorageUnavailableAndKeepsDraft()
    {
        using var temp = new C6TemporaryDirectory();
        var runId = "c6-fault-decorator";
        var dataRoot = Path.Combine(temp.RootPath, "data");
        var draftDirectory = Path.Combine(temp.RootPath, "drafts");
        await C6DataFactory.SeedAsync(dataRoot, notes: 2, visible: 1, runId);

        var connectionFactory = new SqliteConnectionFactory(Path.Combine(dataRoot, "notes.db"));
        var repository = new SqliteNoteRepository(connectionFactory);
        var draftStore = new FileNoteDraftStore(draftDirectory);
        var note = (await repository.ListAsync(NoteLifecycle.Active)).First();
        var tagIds = await repository.GetTagIdsAsync(note.Id);
        var updated = note with
        {
            Body = $"{C6DataFactory.SyntheticBodyMarker} run={runId} update",
            ModifiedAtUtc = DateTimeOffset.UtcNow
        };
        var draft = new NoteDraft
        {
            DraftId = Guid.NewGuid(),
            NoteId = note.Id,
            BaseVersion = note.Version,
            Title = updated.Title,
            Body = updated.Body,
            CategoryId = updated.CategoryId,
            TagIds = tagIds,
            IsPinned = updated.IsPinned,
            DisplayIntent = updated.DisplayIntent,
            CapturedAtUtc = DateTimeOffset.UtcNow
        };
        await draftStore.SaveAsync(draft);

        var coordinator = new NoteSaveCoordinator(
            new FaultingNoteRepository(repository, HarnessFaultMode.DiskFull),
            draftStore);
        var result = await coordinator.UpdateAsync(
            updated,
            note.Version,
            tagIds,
            draft.DraftId);

        Assert.Equal(SaveStatus.StorageUnavailable, result.Status);
        Assert.NotEqual(SaveStatus.Saved, result.Status);
        Assert.Equal(SaveErrorCode.StorageUnavailable, result.Error!.Code);
        Assert.Equal(DraftDisposition.Retained, result.DraftDisposition);
        Assert.Equal(draft.DraftId, result.DraftId);
        Assert.Equal(1, result.Attempts);

        var stored = await repository.GetAsync(note.Id);
        Assert.Equal(note.Body, stored!.Body);
        Assert.NotNull(await draftStore.GetAsync(draft.DraftId));
    }

    [Fact]
    public async Task SaveScenario_DiskFull_ReportsFaultAndReopensWithDraft()
    {
        using var temp = new C6TemporaryDirectory();
        var options = ParseSave(temp, runId: "c6-f01", faultDiskFull: true);

        var result = await SaveScenario.RunAsync(options, CancellationToken.None)
            .WaitAsync(TestTimeout);

        Assert.True(result.Passed);
        Assert.Equal("fault_observed", result.Status);
        Assert.Equal(SaveStatus.StorageUnavailable, result.SaveStatus);
        Assert.False(result.SavedReported);
        Assert.True(result.DraftSaved);
        Assert.True(result.DraftRetained);
        Assert.False(result.NoteSaved);
        Assert.True(result.DatabaseReadable);
        Assert.NotNull(result.Restart);
        Assert.True(result.Restart!.Passed);
        Assert.True(result.Restart.DatabaseReadable);
        Assert.True(result.Restart.NotePresent);
        Assert.True(result.Restart.DraftPresent);
    }

    [Fact]
    public async Task SaveScenario_WithoutFault_SavesNoteAndDeletesDraft()
    {
        using var temp = new C6TemporaryDirectory();
        var options = ParseSave(temp, runId: "c6-save-ok", faultDiskFull: false);

        var result = await SaveScenario.RunAsync(options, CancellationToken.None)
            .WaitAsync(TestTimeout);

        Assert.True(result.Passed);
        Assert.Equal("ok", result.Status);
        Assert.Equal(SaveStatus.Saved, result.SaveStatus);
        Assert.True(result.SavedReported);
        Assert.True(result.NoteSaved);
        Assert.False(result.DraftRetained);
        Assert.Equal(DraftDisposition.DeletedAfterSave, result.DraftDisposition);
        Assert.True(result.DatabaseReadable);
        Assert.NotNull(result.Restart);
        Assert.True(result.Restart!.Passed);
    }

    [Fact]
    public async Task SaveScenario_DraftFailureIsVisibleAndRetrySucceedsAfterRepair()
    {
        using var temp = new C6TemporaryDirectory();
        var draftPath = Path.Combine(temp.RootPath, "blocked-drafts");
        Directory.CreateDirectory(Path.GetDirectoryName(draftPath)!);
        await File.WriteAllTextAsync(draftPath, "not a directory");
        var options = ParseSave(
            temp,
            runId: "c6-f02-repair",
            faultDiskFull: false,
            draftDirectory: draftPath);

        var failed = await SaveScenario.RunAsync(options, CancellationToken.None)
            .WaitAsync(TestTimeout);

        Assert.False(failed.Passed);
        Assert.Equal("draft_failure", failed.Status);
        Assert.Equal("draft", failed.FailureStage);
        Assert.False(failed.DraftSaved);
        Assert.False(failed.NoteSaved);
        Assert.True(failed.DatabaseReadable);
        Assert.NotNull(failed.Restart);
        Assert.True(failed.Restart!.DatabaseReadable);
        Assert.True(failed.Restart.NotePresent);

        File.Delete(draftPath);
        var retried = await SaveScenario.RunAsync(options, CancellationToken.None)
            .WaitAsync(TestTimeout);

        Assert.True(retried.Passed);
        Assert.Equal(SaveStatus.Saved, retried.SaveStatus);
        Assert.True(retried.NoteSaved);
        Assert.False(retried.DraftRetained);
    }

    [Fact]
    public async Task BackupScenario_OfflineDestination_FailsWithoutPublishingAndKeepsExistingPackage()
    {
        using var temp = new C6TemporaryDirectory();
        var dataRoot = Path.Combine(temp.RootPath, "data");
        var backupDirectory = Path.Combine(temp.RootPath, "backups");
        Directory.CreateDirectory(backupDirectory);
        await C6DataFactory.SeedAsync(dataRoot, notes: 2, visible: 1, "c6-f03");

        var baseline = await BackupScenario.RunAsync(
                ParseBackup(temp, "c6-f03", faultOffline: false),
                CancellationToken.None)
            .WaitAsync(TestTimeout);
        Assert.True(baseline.Passed);
        Assert.True(baseline.Backup!.NewPackagePublished);
        Assert.Equal(1, baseline.Backup.PackageCountAfter);

        var faulted = await BackupScenario.RunAsync(
                ParseBackup(temp, "c6-f03", faultOffline: true),
                CancellationToken.None)
            .WaitAsync(TestTimeout);

        Assert.True(faulted.Passed);
        Assert.Equal("fault_observed", faulted.Status);
        Assert.Equal(BackupStatus.StorageUnavailable, faulted.Backup!.Status);
        Assert.Equal(BackupErrorCode.DestinationUnavailable, faulted.Backup.ErrorCode);
        Assert.False(faulted.Backup.NewPackagePublished);
        Assert.True(faulted.Backup.ExistingPackagesIntact);
        Assert.Equal(1, faulted.Backup.PackageCountBefore);
        Assert.Equal(1, faulted.Backup.PackageCountAfter);
        Assert.Equal(0, faulted.Backup.TemporaryResidueCount);
        Assert.True(faulted.DatabaseReadable);
        Assert.True(faulted.Restart!.Passed);

        var existingPackage = Assert.Single(Directory.GetFiles(backupDirectory, "quicknotes-*.zip"));
        var manifest = await new QuickNotes.Infrastructure.Backup.BackupPackageVerifier()
            .VerifyAsync(existingPackage, backupDirectory);
        Assert.Equal(QuickNotes.Application.BackupType.Manual, manifest.BackupType);
    }

    [Fact]
    public async Task FaultingNoteRepository_ReadsPassThroughButWritesReportSqliteFull()
    {
        using var temp = new C6TemporaryDirectory();
        var dataRoot = Path.Combine(temp.RootPath, "data");
        await C6DataFactory.SeedAsync(dataRoot, notes: 1, visible: 1, "c6-decorator-reads");
        var repository = new SqliteNoteRepository(
            new SqliteConnectionFactory(Path.Combine(dataRoot, "notes.db")));
        var faulted = new FaultingNoteRepository(repository, HarnessFaultMode.DiskFull);
        var note = (await faulted.ListAsync(NoteLifecycle.Active)).Single();

        var exception = await Assert.ThrowsAsync<StorageUnavailableException>(
            () => faulted.UpdateAsync(note, note.Version, Array.Empty<Guid>()));

        Assert.Equal("sqlite_full", exception.ErrorCode);
        Assert.False(exception.IsRetryable);
        Assert.Equal(note.Id, (await faulted.GetAsync(note.Id))!.Id);
    }

    private static HarnessOptions ParseSave(
        C6TemporaryDirectory temp,
        string runId,
        bool faultDiskFull,
        string? draftDirectory = null)
    {
        var args = new List<string>
        {
            "save",
            "--data-root", Path.Combine(temp.RootPath, "data"),
            "--checkpoint-dir", Path.Combine(temp.RootPath, "checkpoints"),
            "--run-id", runId,
            "--notes", "2",
            "--visible", "1"
        };
        if (draftDirectory is not null)
        {
            args.Add("--draft-dir");
            args.Add(draftDirectory);
        }

        if (faultDiskFull)
        {
            args.Add("--fault-disk-full");
        }

        return HarnessOptions.Parse(args.ToArray());
    }

    private static HarnessOptions ParseBackup(
        C6TemporaryDirectory temp,
        string runId,
        bool faultOffline)
    {
        var args = new List<string>
        {
            "backup",
            "--data-root", Path.Combine(temp.RootPath, "data"),
            "--backup-dir", Path.Combine(temp.RootPath, "backups"),
            "--checkpoint-dir", Path.Combine(temp.RootPath, "checkpoints"),
            "--run-id", runId,
            "--notes", "2",
            "--visible", "1"
        };
        if (faultOffline)
        {
            args.Add("--fault-offline");
        }

        return HarnessOptions.Parse(args.ToArray());
    }
}
