using QuickNotes.Application;
using QuickNotes.Desktop.Editing;
using QuickNotes.Desktop.Paths;
using QuickNotes.Domain;
using QuickNotes.Infrastructure.Drafts;
using QuickNotes.Infrastructure.Sqlite;

namespace QuickNotes.Desktop.Tests;

public sealed class EditorPersistenceIntegrationTests
{
    [Fact]
    public async Task NewNote_EditSaveReopen_RoundTrips()
    {
        using var temporary = new TestTemporaryDirectory();
        var context = await EditorContext.CreateAsync(temporary);
        var session = await context.Workspace.CreateNewAsync();
        session.ChangeText("persisted title", "persisted body");
        var saved = await session.SaveNowAsync();
        Assert.Equal(SaveStatus.Saved, saved.Status);
        var noteId = session.NoteId;
        await session.DisposeAsync();

        var reopened = await EditorContext.CreateAsync(temporary);
        var note = await reopened.Notes.GetAsync(noteId);
        var openSession = await reopened.Workspace.OpenAsync(noteId);

        Assert.NotNull(note);
        Assert.Equal("persisted title", note!.Title);
        Assert.Equal("persisted body", note.Body);
        Assert.NotNull(openSession);
        Assert.Equal("persisted body", openSession!.Body);
        Assert.Equal(1, openSession.Status.BaseVersion);
    }

    [Fact]
    public async Task DraftSurvivesRestart_AndCanBeRestored()
    {
        using var temporary = new TestTemporaryDirectory();
        var context = await EditorContext.CreateAsync(temporary);
        var session = await context.Workspace.CreateNewAsync();
        session.ChangeText("draft title", "draft body");
        await session.FlushDraftAsync();
        var noteId = session.NoteId;
        var draftId = session.DraftId;
        await session.DisposeAsync();

        var restarted = await EditorContext.CreateAsync(temporary);
        var inventory = await restarted.Recovery.ListAsync();
        var item = Assert.Single(inventory.Items);
        Assert.False(item.NoteExists);
        var restored = await restarted.Recovery.OpenAsync(item.Draft);

        Assert.True(restored.IsNew);
        Assert.Equal(noteId, restored.NoteId);
        Assert.Equal(draftId, restored.DraftId);
        Assert.Equal("draft body", restored.Body);
        Assert.True(restored.Status.HasPendingChanges);
        var saved = await restored.SaveNowAsync();
        Assert.Equal(SaveStatus.Saved, saved.Status);
        Assert.Equal("draft body", (await restarted.Notes.GetAsync(noteId))!.Body);
        Assert.Null(await restarted.Drafts.GetAsync(draftId));
    }

    [Fact]
    public async Task Conflict_TwoSessions_PreservesBoth()
    {
        using var temporary = new TestTemporaryDirectory();
        var context = await EditorContext.CreateAsync(temporary);
        var original = await context.Workspace.CreateNewAsync();
        original.ChangeText("title", "original");
        Assert.Equal(SaveStatus.Saved, (await original.SaveNowAsync()).Status);
        var noteId = original.NoteId;
        await original.DisposeAsync();

        var first = (await context.Workspace.OpenAsync(noteId))!;
        var second = (await context.Workspace.OpenAsync(noteId))!;
        first.ChangeText("title", "first writer");
        Assert.Equal(SaveStatus.Saved, (await first.SaveNowAsync()).Status);
        second.ChangeText("title", "second writer");
        var conflict = await second.SaveNowAsync();

        Assert.Equal(SaveStatus.Conflict, conflict.Status);
        Assert.NotNull(second.Conflict);
        Assert.Equal("second writer", second.Body);
        Assert.Equal("first writer", (await context.Notes.GetAsync(noteId))!.Body);
    }

    [Fact]
    public async Task TwoSessions_DifferentNotes_SaveIndependently()
    {
        using var temporary = new TestTemporaryDirectory();
        var context = await EditorContext.CreateAsync(temporary);
        var first = await context.Workspace.CreateNewAsync();
        var second = await context.Workspace.CreateNewAsync();
        first.ChangeText("first", "first body");
        second.ChangeText("second", "second body");

        var results = await Task.WhenAll(first.SaveNowAsync(), second.SaveNowAsync());

        Assert.All(results, result => Assert.Equal(SaveStatus.Saved, result.Status));
        Assert.Equal("first body", (await context.Notes.GetAsync(first.NoteId))!.Body);
        Assert.Equal("second body", (await context.Notes.GetAsync(second.NoteId))!.Body);
        Assert.Equal(2, (await context.Notes.ListAsync(NoteLifecycle.Active)).Count);
        await first.DisposeAsync();
        await second.DisposeAsync();
    }

    [Fact]
    public async Task SaveWhileEditing_NewerEditNotReportedSaved()
    {
        using var temporary = new TestTemporaryDirectory();
        var context = await EditorContext.CreateAsync(temporary);
        var created = await context.Workspace.CreateNewAsync();
        created.ChangeText("title", "first");
        Assert.Equal(SaveStatus.Saved, (await created.SaveNowAsync()).Status);
        var noteId = created.NoteId;
        await created.DisposeAsync();

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gatedNotes = new GatedNoteRepository(context.Notes, gate.Task);
        var saves = new NoteSaveCoordinator(
            gatedNotes,
            context.Drafts,
            writeGate: new DatabaseWriteGate());
        var workspace = new EditorWorkspaceService(
            gatedNotes,
            saves,
            context.Drafts,
            LongSaveOptions());
        var session = (await workspace.OpenAsync(noteId))!;

        session.ChangeText("title", "first edit");
        var saveTask = session.SaveNowAsync();
        await gatedNotes.UpdateStarted.Task;
        session.ChangeText("title", "second edit");
        gate.SetResult();
        Assert.Equal(SaveStatus.Saved, (await saveTask).Status);

        Assert.Equal("second edit", session.Body);
        Assert.NotEqual(EditorSaveState.Saved, session.Status.SaveState);
        Assert.True(session.HasPendingChanges);

        Assert.Equal(SaveStatus.Saved, (await session.SaveNowAsync()).Status);
        Assert.Equal("second edit", (await context.Notes.GetAsync(noteId))!.Body);
    }

    [Fact]
    public async Task DraftRecovery_DoesNotAutoSubmit()
    {
        using var temporary = new TestTemporaryDirectory();
        var context = await EditorContext.CreateAsync(temporary);
        var noteId = Guid.NewGuid();
        var draftId = Guid.NewGuid();
        await context.Drafts.SaveAsync(new NoteDraft
        {
            DraftId = draftId,
            NoteId = noteId,
            BaseVersion = 0,
            Title = "recovered",
            Body = "recovered body",
            CategoryId = Category.UncategorizedId,
            TagIds = Array.Empty<Guid>(),
            CapturedAtUtc = DateTimeOffset.UtcNow
        });
        var workspace = new EditorWorkspaceService(
            context.Notes,
            new NoteSaveCoordinator(
                context.Notes,
                context.Drafts,
                writeGate: new DatabaseWriteGate()),
            context.Drafts,
            new EditorAutosaveOptions
            {
                DraftDebounce = TimeSpan.FromMilliseconds(10),
                DraftMaxDelay = TimeSpan.FromSeconds(1),
                SaveDebounce = TimeSpan.FromMilliseconds(10),
                SaveMaxDelay = TimeSpan.FromSeconds(1)
            });
        var recovery = new DraftRecoveryService(context.Paths, context.Notes, context.Drafts, workspace);
        var inventory = await recovery.ListAsync();
        var session = await recovery.OpenAsync(Assert.Single(inventory.Items).Draft);

        await Task.Delay(200);

        Assert.True(session.HasPendingChanges);
        Assert.Empty(await context.Notes.ListAsync(NoteLifecycle.Active));
        Assert.NotNull(await context.Drafts.GetAsync(draftId));
        await session.DisposeAsync();
    }

    [Fact]
    public async Task UnknownOutcome_ReadbackPreventsDuplicateNote()
    {
        using var temporary = new TestTemporaryDirectory();
        var context = await EditorContext.CreateAsync(temporary);
        var real = new NoteSaveCoordinator(
            context.Notes,
            context.Drafts,
            writeGate: new DatabaseWriteGate());
        var coordinator = new UnknownOnceCoordinator(real);
        var workspace = new EditorWorkspaceService(
            context.Notes,
            coordinator,
            context.Drafts,
            LongSaveOptions());
        var session = await workspace.CreateNewAsync();

        session.ChangeText("unknown title", "unknown body");
        var first = await session.SaveNowAsync();

        Assert.Equal(SaveStatus.OutcomeUnknown, first.Status);
        Assert.Equal(EditorSaveState.OutcomeUnknown, session.Status.SaveState);

        var resolution = await session.RetryAsync();

        Assert.Equal(EditorSaveResolutionStatus.Saved, resolution.Status);
        Assert.Equal(1, coordinator.CreateCalls);
        Assert.Single(await context.Notes.ListAsync(NoteLifecycle.Active));
        Assert.Equal("unknown body", (await context.Notes.GetAsync(session.NoteId))!.Body);
    }

    [Fact]
    public async Task CloseWithFailedSave_RetainsDraftAndContent()
    {
        using var temporary = new TestTemporaryDirectory();
        var context = await EditorContext.CreateAsync(temporary);
        var workspace = new EditorWorkspaceService(
            context.Notes,
            new FailingCoordinator(),
            context.Drafts,
            LongSaveOptions());
        var session = await workspace.CreateNewAsync();
        session.ChangeText("unsaved title", "unsaved body");
        await session.FlushDraftAsync();

        var result = await session.SaveNowAsync();

        Assert.Equal(SaveStatus.StorageUnavailable, result.Status);
        Assert.Equal(EditorSaveState.SaveFailed, session.Status.SaveState);
        Assert.True(session.HasPendingChanges);
        var draftId = session.DraftId;
        await session.DisposeAsync();

        var restarted = await EditorContext.CreateAsync(temporary);
        var draft = await restarted.Drafts.GetAsync(draftId);
        Assert.NotNull(draft);
        Assert.Equal("unsaved body", draft!.Body);
        Assert.Empty(await restarted.Notes.ListAsync(NoteLifecycle.Active));
    }

    private static EditorAutosaveOptions LongSaveOptions() => new()
    {
        DraftDebounce = TimeSpan.FromMilliseconds(10),
        DraftMaxDelay = TimeSpan.FromSeconds(1),
        SaveDebounce = TimeSpan.FromHours(1),
        SaveMaxDelay = TimeSpan.FromHours(2)
    };

    private sealed class GatedNoteRepository : INoteRepository
    {
        private readonly INoteRepository _inner;
        private readonly Task _gate;

        public GatedNoteRepository(INoteRepository inner, Task gate)
        {
            _inner = inner;
            _gate = gate;
        }

        public TaskCompletionSource UpdateStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Note> CreateAsync(
            Note note,
            IReadOnlyCollection<Guid> tagIds,
            CancellationToken cancellationToken = default) =>
            _inner.CreateAsync(note, tagIds, cancellationToken);

        public Task<Note?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            _inner.GetAsync(id, cancellationToken);

        public Task<IReadOnlyList<Note>> ListAsync(
            NoteLifecycle lifecycle,
            CancellationToken cancellationToken = default) =>
            _inner.ListAsync(lifecycle, cancellationToken);

        public Task<IReadOnlyList<Guid>> GetTagIdsAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            _inner.GetTagIdsAsync(id, cancellationToken);

        public async Task<Note> UpdateAsync(
            Note note,
            long expectedVersion,
            IReadOnlyCollection<Guid> tagIds,
            CancellationToken cancellationToken = default)
        {
            UpdateStarted.TrySetResult();
            await _gate.WaitAsync(cancellationToken);
            return await _inner.UpdateAsync(note, expectedVersion, tagIds, cancellationToken);
        }

        public Task<Note> SetLifecycleAsync(
            Guid id,
            NoteLifecycle lifecycle,
            long expectedVersion,
            DateTimeOffset modifiedAtUtc,
            CancellationToken cancellationToken = default) =>
            _inner.SetLifecycleAsync(id, lifecycle, expectedVersion, modifiedAtUtc, cancellationToken);
    }

    private sealed class UnknownOnceCoordinator : INoteSaveCoordinator
    {
        private readonly INoteSaveCoordinator _inner;

        public UnknownOnceCoordinator(INoteSaveCoordinator inner)
        {
            _inner = inner;
        }

        public int CreateCalls { get; private set; }

        public async Task<SaveResult> CreateAsync(
            Note note,
            IReadOnlyCollection<Guid> tagIds,
            Guid? draftId = null,
            CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            var result = await _inner.CreateAsync(note, tagIds, draftId, cancellationToken);
            return new SaveResult
            {
                Status = SaveStatus.OutcomeUnknown,
                NoteId = result.NoteId,
                SavedNote = result.SavedNote,
                CurrentNote = result.SavedNote,
                CurrentVersion = result.SavedNote?.Version,
                DraftId = draftId,
                DraftDisposition = result.DraftDisposition,
                Attempts = 1,
                Error = new SaveError(
                    SaveErrorCode.CommitOutcomeUnknown,
                    "simulated unknown outcome",
                    false)
            };
        }

        public Task<SaveResult> UpdateAsync(
            Note note,
            long expectedVersion,
            IReadOnlyCollection<Guid> tagIds,
            Guid? draftId = null,
            CancellationToken cancellationToken = default) =>
            _inner.UpdateAsync(note, expectedVersion, tagIds, draftId, cancellationToken);

        public Task<SaveResult> SetLifecycleAsync(
            Guid id,
            NoteLifecycle lifecycle,
            long expectedVersion,
            DateTimeOffset modifiedAtUtc,
            CancellationToken cancellationToken = default) =>
            _inner.SetLifecycleAsync(id, lifecycle, expectedVersion, modifiedAtUtc, cancellationToken);
    }

    private sealed class FailingCoordinator : INoteSaveCoordinator
    {
        public Task<SaveResult> CreateAsync(
            Note note,
            IReadOnlyCollection<Guid> tagIds,
            Guid? draftId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Failed(note.Id, draftId));

        public Task<SaveResult> UpdateAsync(
            Note note,
            long expectedVersion,
            IReadOnlyCollection<Guid> tagIds,
            Guid? draftId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Failed(note.Id, draftId));

        public Task<SaveResult> SetLifecycleAsync(
            Guid id,
            NoteLifecycle lifecycle,
            long expectedVersion,
            DateTimeOffset modifiedAtUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private static SaveResult Failed(Guid noteId, Guid? draftId) => new()
        {
            Status = SaveStatus.StorageUnavailable,
            NoteId = noteId,
            DraftId = draftId,
            Error = new SaveError(
                SaveErrorCode.StorageUnavailable,
                "simulated storage failure",
                false)
        };
    }

    private sealed class EditorContext
    {
        private EditorContext(
            DesktopPaths paths,
            SqliteNoteRepository notes,
            FileNoteDraftStore drafts,
            EditorWorkspaceService workspace,
            DraftRecoveryService recovery)
        {
            Paths = paths;
            Notes = notes;
            Drafts = drafts;
            Workspace = workspace;
            Recovery = recovery;
        }

        public DesktopPaths Paths { get; }
        public SqliteNoteRepository Notes { get; }
        public FileNoteDraftStore Drafts { get; }
        public EditorWorkspaceService Workspace { get; }
        public DraftRecoveryService Recovery { get; }

        public static async Task<EditorContext> CreateAsync(TestTemporaryDirectory temporary)
        {
            var paths = DesktopPaths.Create(Path.Combine(temporary.RootPath, "data"));
            paths.EnsureDirectories();
            var factory = new SqliteConnectionFactory(paths.DatabasePath);
            await new SqliteSchema(factory).InitializeAsync();
            var notes = new SqliteNoteRepository(factory);
            var drafts = new FileNoteDraftStore(paths.DraftDirectory);
            var saves = new NoteSaveCoordinator(
                notes,
                drafts,
                writeGate: new DatabaseWriteGate());
            var workspace = new EditorWorkspaceService(notes, saves, drafts);
            var recovery = new DraftRecoveryService(paths, notes, drafts, workspace);
            return new EditorContext(paths, notes, drafts, workspace, recovery);
        }
    }
}
