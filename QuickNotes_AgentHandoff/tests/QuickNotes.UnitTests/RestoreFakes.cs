using QuickNotes.Application;

namespace QuickNotes.UnitTests;

internal sealed class FakeBackupPackageExtractor : IBackupPackageExtractor
{
    public Func<string, string, CancellationToken, Task<BackupManifest>> Handler { get; set; } =
        static (_, _, _) => Task.FromResult(BackupTestData.Manifest());

    public int Calls { get; private set; }
    public string? LastPackagePath { get; private set; }
    public string? LastDestinationPath { get; private set; }

    public Task<BackupManifest> ExtractAsync(
        string packagePath,
        string destinationDatabasePath,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        LastPackagePath = packagePath;
        LastDestinationPath = destinationDatabasePath;
        return Handler(packagePath, destinationDatabasePath, cancellationToken);
    }
}

internal sealed class FakeRestoreStore : IRestoreStore
{
    public string CurrentDatabasePath { get; set; } = string.Empty;
    public RestoreDatabaseInfo CurrentInfo { get; set; } = new(1, Counts(1, 0, 0, 1, 0, 0));
    public RestoreDatabaseInfo StagedInfo { get; set; } = new(1, Counts(2, 0, 0, 1, 1, 1));
    public RestoreDatabaseInfo MismatchedInfo { get; set; } = new(1, Counts(99, 0, 0, 1, 0, 0));
    public RestoreException? PrepareFailure { get; set; }
    public RestoreException? SwitchFailure { get; set; }
    public RestoreException? RollbackFailure { get; set; }
    public bool SwitchThrowsAfterSwitching { get; set; }
    public bool MismatchAfterSwitch { get; set; }
    public string CurrentContentHash { get; set; } = new string('b', 64);
    public string StagedContentHash { get; set; } = BackupTestData.Manifest().DatabaseSha256;
    public bool Switched { get; private set; }
    public int InspectCalls { get; private set; }
    public int PrepareCalls { get; private set; }
    public int SwitchCalls { get; private set; }
    public int RollbackCalls { get; private set; }

    public Task<string?> TryGetDatabaseHashAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(databasePath, CurrentDatabasePath, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<string?>(Switched ? StagedContentHash : CurrentContentHash);
        }

        return Task.FromResult<string?>(StagedContentHash);
    }

    public Task<RestoreDatabaseInfo> InspectAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        InspectCalls++;
        if (string.Equals(databasePath, CurrentDatabasePath, StringComparison.OrdinalIgnoreCase))
        {
            if (Switched)
            {
                return Task.FromResult(MismatchAfterSwitch ? MismatchedInfo : StagedInfo);
            }

            return Task.FromResult(CurrentInfo);
        }

        return Task.FromResult(StagedInfo);
    }

    public Task<RestoreDatabaseInfo> PrepareCurrentAsync(
        string currentDatabasePath,
        CancellationToken cancellationToken = default)
    {
        PrepareCalls++;
        if (PrepareFailure is not null)
        {
            throw PrepareFailure;
        }

        return Task.FromResult(CurrentInfo);
    }

    public Task SwitchAsync(
        string stagedDatabasePath,
        string currentDatabasePath,
        string rollbackDatabasePath,
        CancellationToken cancellationToken = default)
    {
        SwitchCalls++;
        if (SwitchFailure is not null)
        {
            if (SwitchThrowsAfterSwitching)
            {
                Switched = true;
            }

            throw SwitchFailure;
        }

        Switched = true;
        return Task.CompletedTask;
    }

    public Task RollbackAsync(
        string rollbackDatabasePath,
        string currentDatabasePath,
        string failedDatabasePath,
        CancellationToken cancellationToken = default)
    {
        RollbackCalls++;
        if (RollbackFailure is not null)
        {
            throw RollbackFailure;
        }

        Switched = false;
        return Task.CompletedTask;
    }

    public static RestoreCounts Counts(
        long active,
        long archived,
        long trash,
        long categories,
        long tags,
        long noteTags) =>
        new(active, archived, trash, categories, tags, noteTags);
}

internal sealed class FakeRestoreDraftStore : INoteDraftStore
{
    public List<NoteDraft> Drafts { get; } = new();
    public Exception? ListFailure { get; set; }
    public int ListCalls { get; private set; }
    public int DeleteCalls { get; private set; }

    public Task SaveAsync(NoteDraft draft, CancellationToken cancellationToken = default)
    {
        Drafts.Add(draft);
        return Task.CompletedTask;
    }

    public Task<NoteDraft?> GetAsync(
        Guid draftId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Drafts.FirstOrDefault(draft => draft.DraftId == draftId));

    public Task<IReadOnlyList<NoteDraft>> ListAsync(
        Guid? noteId = null,
        CancellationToken cancellationToken = default)
    {
        ListCalls++;
        if (ListFailure is not null)
        {
            throw ListFailure;
        }

        IReadOnlyList<NoteDraft> drafts = Drafts
            .Where(draft => noteId is null || draft.NoteId == noteId)
            .ToArray();
        return Task.FromResult(drafts);
    }

    public Task DeleteAsync(Guid draftId, CancellationToken cancellationToken = default)
    {
        DeleteCalls++;
        Drafts.RemoveAll(draft => draft.DraftId == draftId);
        return Task.CompletedTask;
    }
}

internal sealed class FakeDiskSpaceProvider : IDiskSpaceProvider
{
    public long AvailableBytes { get; set; } = long.MaxValue / 2;
    public RestoreException? Failure { get; set; }

    public long GetAvailableBytes(string path)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        return AvailableBytes;
    }
}
