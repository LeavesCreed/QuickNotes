using QuickNotes.Infrastructure.Sqlite;

namespace QuickNotes.C6.Harness;

/// <summary>
/// Prepares a disposable C6 data root with synthetic v2 data and a ready
/// application state. The kill driver uses it before starting a scenario that
/// is expected to be killed, so the restart probe measures the stage under
/// test rather than first-run initialization.
/// </summary>
public static class SeedScenario
{
    public static async Task<HarnessScenarioResult> RunAsync(
        HarnessOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Scenario != HarnessScenario.Seed)
        {
            throw new ArgumentException("SeedScenario requires the seed scenario.", nameof(options));
        }

        var runId = C6HarnessPaths.ResolveRunId(options);
        var dataRoot = C6HarnessPaths.RequireUnderRunRoot(options.DataRoot, nameof(options.DataRoot));
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
        var info = await new SqliteRestoreStore()
            .InspectAsync(Path.Combine(dataRoot, "notes.db"), cancellationToken)
            .ConfigureAwait(false);
        var total = checked((int)(
            info.Counts.ActiveNotes + info.Counts.ArchivedNotes + info.Counts.TrashNotes));
        return new HarnessScenarioResult
        {
            Scenario = "seed",
            Status = "ok",
            Passed = true,
            DatabaseReadable = true,
            NoteCount = checked((int)info.Counts.ActiveNotes),
            TotalNoteCount = total,
            Message = "Synthetic C6 data and a ready application state were written."
        };
    }
}
