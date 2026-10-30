using System.Security.Cryptography;
using QuickNotes.Application;
using QuickNotes.Domain;
using QuickNotes.Infrastructure.Backup;
using QuickNotes.Infrastructure.Sqlite;

namespace QuickNotes.C6.Harness;

/// <summary>
/// Real backup scenario. The snapshot and package are produced by the
/// production snapshot provider and ZIP package store; only the offline
/// composition wraps the package store at its publish boundary.
/// </summary>
public static class BackupScenario
{
    public static async Task<HarnessScenarioResult> RunAsync(
        HarnessOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Scenario != HarnessScenario.Backup)
        {
            throw new ArgumentException("BackupScenario requires the backup scenario.", nameof(options));
        }

        var runId = C6HarnessPaths.ResolveRunId(options);
        var dataRoot = C6HarnessPaths.RequireUnderRunRoot(options.DataRoot, nameof(options.DataRoot));
        var backupDirectory = C6HarnessPaths.RequireUnderRunRoot(
            options.BackupDirectory
                ?? throw new ArgumentException("--backup-dir is required for the backup scenario."),
            nameof(options.BackupDirectory));
        var checkpointDirectory = C6HarnessPaths.RequireUnderRunRoot(
            options.CheckpointDirectory
                ?? throw new ArgumentException("--checkpoint-dir is required for the backup scenario."),
            nameof(options.CheckpointDirectory));
        var sink = new CheckpointFileSink(
            checkpointDirectory,
            options.KillMarker,
            holdTimeout: TimeSpan.FromMinutes(2));

        await SaveScenario.EnsureSeededAsync(
            dataRoot,
            options.NoteCount,
            options.VisibleCount,
            runId,
            cancellationToken).ConfigureAwait(false);
        await C6HarnessState.WriteReadyAsync(
            dataRoot,
            schemaVersion: 2,
            runId,
            cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(backupDirectory);

        var databasePath = Path.Combine(dataRoot, "notes.db");
        var before = HashPackages(backupDirectory);
        IBackupPackageStore packageStore = new ZipBackupPackageStore();
        if (options.FaultMode != HarnessFaultMode.None)
        {
            packageStore = new FaultingBackupPackageStore(packageStore, options.FaultMode);
        }

        packageStore = new CheckpointingBackupPackageStore(packageStore, sink);

        var coordinator = new BackupCoordinator(
            new SqliteSnapshotProvider(databasePath),
            packageStore);
        BackupResult backupResult;
        try
        {
            backupResult = await coordinator.CreateAsync(
                    new BackupRequest
                    {
                        BackupDirectory = backupDirectory,
                        Type = BackupType.Manual
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var restart = await ReopenAsync(databasePath, cancellationToken).ConfigureAwait(false);
            return new HarnessScenarioResult
            {
                Scenario = "backup",
                Status = "backup_exception",
                Passed = false,
                FailureStage = "backup",
                ErrorCode = exception.GetType().Name,
                DatabaseReadable = restart.DatabaseReadable,
                NoteCount = restart.NoteCount,
                Restart = restart.Witness,
                Message = "The backup coordinator raised an unexpected exception."
            };
        }

        var after = HashPackages(backupDirectory);
        var newFiles = after.Keys.Except(before.Keys, StringComparer.OrdinalIgnoreCase).ToArray();
        var existingIntact = before.All(
            pair => after.TryGetValue(pair.Key, out var hash)
                && string.Equals(hash, pair.Value, StringComparison.Ordinal));
        var residueCount = CountTemporaryResidue(backupDirectory);
        var reopened = await ReopenAsync(databasePath, cancellationToken).ConfigureAwait(false);
        var newPackagePublished = newFiles.Length == 1;
        var newPackageFileName = newPackagePublished ? newFiles[0] : null;

        var evidence = new BackupScenarioEvidence
        {
            Status = backupResult.Status,
            ErrorCode = backupResult.Error?.Code,
            NewPackagePublished = newPackagePublished,
            ExistingPackagesIntact = existingIntact,
            PackageCountBefore = before.Count,
            PackageCountAfter = after.Count,
            TemporaryResidueCount = residueCount,
            NewPackageFileName = newPackageFileName,
            DurationMilliseconds = backupResult.DurationMilliseconds
        };

        if (options.FaultMode != HarnessFaultMode.None)
        {
            var passed = backupResult.Status == BackupStatus.StorageUnavailable
                && !newPackagePublished
                && existingIntact
                && residueCount == 0
                && reopened.DatabaseReadable;
            return new HarnessScenarioResult
            {
                Scenario = "backup",
                Status = passed ? "fault_observed" : "unexpected_result",
                Passed = passed,
                FailureStage = passed ? null : "backup",
                ErrorCode = backupResult.Error?.Code.ToString(),
                DatabaseReadable = reopened.DatabaseReadable,
                NoteCount = reopened.NoteCount,
                Restart = reopened.Witness,
                Backup = evidence,
                Message = passed
                    ? "The backup failed without publishing a package and left the existing backup intact."
                    : "The injected backup fault did not produce the expected safe failure."
            };
        }

        var verified = false;
        if (backupResult.Status == BackupStatus.Succeeded
            && newPackagePublished
            && newPackageFileName is not null)
        {
            try
            {
                await new BackupPackageVerifier()
                    .VerifyAsync(
                        Path.Combine(backupDirectory, newPackageFileName),
                        backupDirectory,
                        cancellationToken)
                    .ConfigureAwait(false);
                verified = true;
            }
            catch (Exception exception) when (exception is
                BackupException or IOException or UnauthorizedAccessException)
            {
                verified = false;
            }
        }

        var success = backupResult.Status == BackupStatus.Succeeded
            && newPackagePublished
            && existingIntact
            && residueCount == 0
            && verified
            && reopened.DatabaseReadable;
        return new HarnessScenarioResult
        {
            Scenario = "backup",
            Status = success ? "ok" : "unexpected_result",
            Passed = success,
            ErrorCode = backupResult.Error?.Code.ToString(),
            DatabaseReadable = reopened.DatabaseReadable,
            NoteCount = reopened.NoteCount,
            Restart = reopened.Witness,
            Backup = evidence,
            Message = success
                ? "The backup published one verified package and left earlier packages intact."
                : "The backup did not match the expected successful outcome."
        };
    }

    private static async Task<ReopenedDatabase> ReopenAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var repository = new SqliteNoteRepository(new SqliteConnectionFactory(databasePath));
        try
        {
            var notes = await repository.ListAsync(NoteLifecycle.Active, cancellationToken)
                .ConfigureAwait(false);
            return new ReopenedDatabase(
                true,
                notes.Count,
                new RestartEvidence
                {
                    Method = "reopen-services",
                    Passed = true,
                    DatabaseReadable = true,
                    NotePresent = notes.Count > 0,
                    NoteCount = notes.Count
                });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ReopenedDatabase(
                false,
                0,
                new RestartEvidence
                {
                    Method = "reopen-services",
                    Passed = false,
                    DatabaseReadable = false,
                    NotePresent = false,
                    Failure = exception.GetType().Name
                });
        }
    }

    private static Dictionary<string, string> HashPackages(string directory)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(directory, "quicknotes-*.zip"))
        {
            hashes[Path.GetFileName(path)] = Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(path)));
        }

        return hashes;
    }

    private static int CountTemporaryResidue(string directory) =>
        Directory.EnumerateFiles(directory, ".quicknotes-*").Count();

    private sealed record ReopenedDatabase(
        bool DatabaseReadable,
        int NoteCount,
        RestartEvidence Witness);
}
