using QuickNotes.Application;
using QuickNotes.Domain;
using QuickNotes.Infrastructure.Drafts;
using QuickNotes.Infrastructure.Sqlite;

namespace QuickNotes.C6.Harness;

/// <summary>
/// Real save/autosave fault scenario. It seeds through the production schema
/// and repositories, writes a draft through the production file draft store,
/// updates an existing note through <see cref="NoteSaveCoordinator"/>, and
/// reopens new service instances afterwards to record what survived.
/// </summary>
public static class SaveScenario
{
    public static async Task<HarnessScenarioResult> RunAsync(
        HarnessOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Scenario != HarnessScenario.Save)
        {
            throw new ArgumentException("SaveScenario requires the save scenario.", nameof(options));
        }

        var runId = C6HarnessPaths.ResolveRunId(options);
        var dataRoot = C6HarnessPaths.RequireUnderRunRoot(options.DataRoot, nameof(options.DataRoot));
        var draftDirectory = C6HarnessPaths.RequireUnderRunRoot(
            options.DraftDirectory ?? Path.Combine(dataRoot, "drafts"),
            nameof(options.DraftDirectory));
        var checkpointDirectory = C6HarnessPaths.RequireUnderRunRoot(
            options.CheckpointDirectory
                ?? throw new ArgumentException("--checkpoint-dir is required for the save scenario."),
            nameof(options.CheckpointDirectory));

        await EnsureSeededAsync(
            dataRoot,
            options.NoteCount,
            options.VisibleCount,
            runId,
            cancellationToken).ConfigureAwait(false);

        var databasePath = Path.Combine(dataRoot, "notes.db");
        var repository = new SqliteNoteRepository(new SqliteConnectionFactory(databasePath));
        var draftStore = new FileNoteDraftStore(draftDirectory);
        var note = await GetOrCreateNoteAsync(repository, runId, cancellationToken)
            .ConfigureAwait(false);
        var tagIds = await repository.GetTagIdsAsync(note.Id, cancellationToken)
            .ConfigureAwait(false);
        var updated = note with
        {
            Body = $"{C6DataFactory.SyntheticBodyMarker} run={runId} update {Guid.NewGuid():N}",
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
            ColorMode = updated.ColorMode,
            ColorHex = updated.ColorHex,
            FontSize = updated.FontSize,
            WindowLayout = updated.WindowLayout,
            CapturedAtUtc = DateTimeOffset.UtcNow
        };

        var checkpoint = new CheckpointFileSink(checkpointDirectory);
        try
        {
            await draftStore.SaveAsync(draft, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is
            DraftStoreException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            var restart = await ReopenAsync(
                databasePath,
                draftDirectory,
                note.Id,
                draft.DraftId,
                expectedBody: null,
                cancellationToken).ConfigureAwait(false);
            return new HarnessScenarioResult
            {
                Scenario = "save",
                Status = "draft_failure",
                Passed = false,
                FailureStage = "draft",
                ErrorCode = exception is DraftStoreException draftException
                    ? draftException.ErrorCode
                    : exception.GetType().Name,
                DraftSaved = false,
                DraftRetained = null,
                NoteSaved = false,
                DatabaseReadable = restart.DatabaseReadable,
                NoteCount = restart.NoteCount,
                Restart = restart.Witness,
                Message = "The draft could not be persisted; no save was attempted."
            };
        }

        checkpoint.Reached("save.after_draft");
        var saveRepository = options.FaultMode == HarnessFaultMode.None
            ? (INoteRepository)repository
            : new FaultingNoteRepository(repository, options.FaultMode);
        var coordinator = new NoteSaveCoordinator(saveRepository, draftStore);
        checkpoint.Reached("save.before_commit");

        SaveResult saveResult;
        try
        {
            saveResult = await coordinator.UpdateAsync(
                    updated,
                    note.Version,
                    tagIds,
                    draft.DraftId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var restart = await ReopenAsync(
                databasePath,
                draftDirectory,
                note.Id,
                draft.DraftId,
                expectedBody: null,
                cancellationToken).ConfigureAwait(false);
            return new HarnessScenarioResult
            {
                Scenario = "save",
                Status = "save_exception",
                Passed = false,
                FailureStage = "save",
                ErrorCode = exception.GetType().Name,
                DraftSaved = true,
                NoteSaved = false,
                DatabaseReadable = restart.DatabaseReadable,
                NoteCount = restart.NoteCount,
                Restart = restart.Witness,
                Message = "The save coordinator raised an unexpected exception."
            };
        }

        checkpoint.Reached("save.after_commit");
        var reopened = await ReopenAsync(
            databasePath,
            draftDirectory,
            note.Id,
            draft.DraftId,
            updated.Body,
            cancellationToken).ConfigureAwait(false);
        var savedReported = saveResult.Status == SaveStatus.Saved;
        var noteSaved = reopened.NotePresent && reopened.NoteBodyMatches;
        var draftRetained = reopened.DraftPresent == true;

        if (options.FaultMode != HarnessFaultMode.None)
        {
            var passed = saveResult.Status == SaveStatus.StorageUnavailable
                && !savedReported
                && draftRetained
                && !noteSaved;
            return new HarnessScenarioResult
            {
                Scenario = "save",
                Status = passed ? "fault_observed" : "unexpected_result",
                Passed = passed,
                FailureStage = passed ? null : "save",
                ErrorCode = saveResult.Error?.Code.ToString(),
                SaveStatus = saveResult.Status,
                SavedReported = savedReported,
                DraftDisposition = saveResult.DraftDisposition,
                Attempts = saveResult.Attempts,
                DraftSaved = true,
                DraftRetained = draftRetained,
                NoteSaved = noteSaved,
                DatabaseReadable = reopened.DatabaseReadable,
                NoteCount = reopened.NoteCount,
                Restart = reopened.Witness,
                Message = passed
                    ? "The save did not report success and the draft remained recoverable."
                    : "The injected save fault did not produce the expected safe failure."
            };
        }

        var success = savedReported
            && noteSaved
            && !draftRetained
            && saveResult.DraftDisposition is
                DraftDisposition.DeletedAfterSave or DraftDisposition.AlreadyAbsent;
        return new HarnessScenarioResult
        {
            Scenario = "save",
            Status = success ? "ok" : "unexpected_result",
            Passed = success,
            ErrorCode = saveResult.Error?.Code.ToString(),
            SaveStatus = saveResult.Status,
            SavedReported = savedReported,
            DraftDisposition = saveResult.DraftDisposition,
            Attempts = saveResult.Attempts,
            DraftSaved = true,
            DraftRetained = draftRetained,
            NoteSaved = noteSaved,
            DatabaseReadable = reopened.DatabaseReadable,
            NoteCount = reopened.NoteCount,
            Restart = reopened.Witness,
            Message = success
                ? "The save committed and the draft was removed."
                : "The save did not match the expected successful outcome."
        };
    }

    public static async Task EnsureSeededAsync(
        string dataRoot,
        int noteCount,
        int visibleCount,
        string runId,
        CancellationToken cancellationToken)
    {
        var databasePath = Path.Combine(dataRoot, "notes.db");
        if (File.Exists(databasePath))
        {
            return;
        }

        await C6DataFactory.SeedAsync(dataRoot, noteCount, visibleCount, runId, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<Note> GetOrCreateNoteAsync(
        INoteRepository repository,
        string runId,
        CancellationToken cancellationToken)
    {
        var active = await repository.ListAsync(NoteLifecycle.Active, cancellationToken)
            .ConfigureAwait(false);
        if (active.Count > 0)
        {
            return active[0];
        }

        var timestamp = DateTimeOffset.UtcNow;
        var note = new Note
        {
            Id = Guid.NewGuid(),
            Title = "C6 synthetic fallback note",
            Body = $"{C6DataFactory.SyntheticBodyMarker} run={runId} fallback",
            CreatedAtUtc = timestamp,
            ModifiedAtUtc = timestamp,
            CategoryId = Category.UncategorizedId
        };
        return await repository.CreateAsync(note, Array.Empty<Guid>(), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<ReopenedState> ReopenAsync(
        string databasePath,
        string draftDirectory,
        Guid noteId,
        Guid draftId,
        string? expectedBody,
        CancellationToken cancellationToken)
    {
        var repository = new SqliteNoteRepository(new SqliteConnectionFactory(databasePath));
        var draftStore = new FileNoteDraftStore(draftDirectory);
        Note? note = null;
        NoteDraft? draft = null;
        string? failure = null;
        try
        {
            note = await repository.GetAsync(noteId, cancellationToken).ConfigureAwait(false);
            draft = await draftStore.GetAsync(draftId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failure = exception.GetType().Name;
        }

        var noteCount = 0;
        try
        {
            noteCount = (await repository.ListAsync(NoteLifecycle.Active, cancellationToken)
                .ConfigureAwait(false)).Count;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failure ??= exception.GetType().Name;
        }

        var databaseReadable = failure is null;
        var notePresent = note is not null;
        var draftPresent = draft is not null;
        var witness = new RestartEvidence
        {
            Method = "reopen-services",
            Passed = databaseReadable && notePresent,
            DatabaseReadable = databaseReadable,
            NotePresent = notePresent,
            DraftPresent = draftPresent,
            NoteCount = noteCount,
            Failure = failure
        };
        return new ReopenedState(
            databaseReadable,
            notePresent,
            expectedBody is not null
                && string.Equals(note?.Body, expectedBody, StringComparison.Ordinal),
            draftPresent,
            noteCount,
            witness);
    }

    private sealed record ReopenedState(
        bool DatabaseReadable,
        bool NotePresent,
        bool NoteBodyMatches,
        bool? DraftPresent,
        int NoteCount,
        RestartEvidence Witness);
}
