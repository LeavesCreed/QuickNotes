using QuickNotes.Application;
using QuickNotes.Desktop.Diagnostics;
using QuickNotes.Desktop.Migration;
using QuickNotes.Desktop.Paths;
using QuickNotes.Infrastructure.Sqlite;

namespace QuickNotes.C6.Harness;

/// <summary>
/// Real v1-to-v2 migration scenario used by C6-K08/K09. It creates a genuine
/// schema-version-1 database with synthetic rows, takes the production
/// protection snapshot through <see cref="SchemaMigrationService"/>, and runs
/// the production <see cref="SqliteSchemaMigrator"/> through checkpoint
/// decorators.
/// </summary>
public static class MigrationScenario
{
    public static async Task<HarnessScenarioResult> RunAsync(
        HarnessOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Scenario != HarnessScenario.Migrate)
        {
            throw new ArgumentException("MigrationScenario requires the migrate scenario.", nameof(options));
        }

        var runId = C6HarnessPaths.ResolveRunId(options);
        var dataRoot = C6HarnessPaths.RequireUnderRunRoot(options.DataRoot, nameof(options.DataRoot));
        var checkpointDirectory = C6HarnessPaths.RequireUnderRunRoot(
            options.CheckpointDirectory
                ?? throw new ArgumentException("--checkpoint-dir is required for the migrate scenario."),
            nameof(options.CheckpointDirectory));
        var databasePath = Path.Combine(dataRoot, "notes.db");

        if (!File.Exists(databasePath))
        {
            await C6DataFactory.SeedVersionOneAsync(
                dataRoot,
                options.NoteCount,
                runId,
                cancellationToken).ConfigureAwait(false);
        }

        var before = await InspectAsync(databasePath, cancellationToken).ConfigureAwait(false);
        if (before is null)
        {
            return Failed("migration_source_unavailable", "The version-one source database could not be inspected.");
        }

        if (before.SchemaVersion == QuickNotes.Infrastructure.Sqlite.SqliteSchema.CurrentVersion)
        {
            return new HarnessScenarioResult
            {
                Scenario = "migrate",
                Status = "already_current",
                Passed = true,
                DatabaseReadable = true,
                NoteCount = checked((int)before.Counts.ActiveNotes),
                TotalNoteCount = Total(before),
                Message = "The data root already uses the current schema; no migration was attempted."
            };
        }

        if (before.SchemaVersion != 1)
        {
            return Failed(
                "migration_source_unsupported",
                $"The source database reports schema {before.SchemaVersion}; only version 1 is supported.");
        }

        var expectedTotal = Total(before);
        await C6HarnessState.WriteReadyAsync(
            dataRoot,
            schemaVersion: 1,
            runId,
            cancellationToken).ConfigureAwait(false);
        var sink = new CheckpointFileSink(
            checkpointDirectory,
            options.KillMarker,
            holdTimeout: TimeSpan.FromMinutes(2));
        var paths = DesktopPaths.Create(dataRoot);
        var service = new SchemaMigrationService(
            paths,
            new CheckpointingSchemaMigrator(new SqliteSchemaMigrator(), sink),
            new SqliteRestoreStore(),
            new DiagnosticLog(paths.LogDirectory));
        var result = await service.EnsureCurrentAsync(1, cancellationToken).ConfigureAwait(false);
        if (result.Status != SchemaMigrationStatus.Migrated)
        {
            return Failed(
                result.ErrorCode ?? "migration_failed",
                $"The migration did not complete: {result.Status}.");
        }

        await C6HarnessState.WriteReadyAsync(
            dataRoot,
            schemaVersion: 2,
            runId,
            cancellationToken).ConfigureAwait(false);
        var after = await InspectAsync(databasePath, cancellationToken).ConfigureAwait(false);
        var readable = after is not null;
        var protectionPreserved = !string.IsNullOrWhiteSpace(result.ProtectionSnapshotPath)
            && File.Exists(result.ProtectionSnapshotPath);
        var passed = readable
            && after!.SchemaVersion == QuickNotes.Infrastructure.Sqlite.SqliteSchema.CurrentVersion
            && Total(after) == expectedTotal
            && protectionPreserved;
        return new HarnessScenarioResult
        {
            Scenario = "migrate",
            Status = passed ? "ok" : "unexpected_result",
            Passed = passed,
            FailureStage = passed ? null : "migration",
            DatabaseReadable = readable,
            NoteCount = readable ? checked((int)after!.Counts.ActiveNotes) : null,
            TotalNoteCount = readable ? Total(after!) : null,
            Message = passed
                ? "The v1 database migrated to the current schema and the protection snapshot was preserved."
                : "The migration did not match the expected successful outcome."
        };
    }

    private static HarnessScenarioResult Failed(string errorCode, string message) => new()
    {
        Scenario = "migrate",
        Status = "migration_failed",
        Passed = false,
        FailureStage = "migration",
        ErrorCode = errorCode,
        DatabaseReadable = false,
        Message = message
    };

    private static async Task<RestoreDatabaseInfo?> InspectAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(databasePath))
        {
            return null;
        }

        try
        {
            return await new SqliteRestoreStore()
                .InspectAsync(databasePath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int Total(RestoreDatabaseInfo info) =>
        checked((int)(
            info.Counts.ActiveNotes + info.Counts.ArchivedNotes + info.Counts.TrashNotes));
}
