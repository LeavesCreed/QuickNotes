using QuickNotes.Application;
using QuickNotes.Domain;

namespace QuickNotes.Desktop.Editing;

public sealed class EditorWorkspaceService
{
    private readonly INoteRepository _notes;
    private readonly INoteSaveCoordinator _saves;
    private readonly INoteDraftStore _drafts;
    private readonly EditorAutosaveOptions _options;
    private readonly TimeProvider _timeProvider;

    public EditorWorkspaceService(
        INoteRepository notes,
        INoteSaveCoordinator saves,
        INoteDraftStore drafts,
        EditorAutosaveOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _notes = notes ?? throw new ArgumentNullException(nameof(notes));
        _saves = saves ?? throw new ArgumentNullException(nameof(saves));
        _drafts = drafts ?? throw new ArgumentNullException(nameof(drafts));
        _options = options ?? new EditorAutosaveOptions();
        _options.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<IReadOnlyList<Note>> ListActiveAsync(CancellationToken cancellationToken = default) =>
        _notes.ListAsync(NoteLifecycle.Active, cancellationToken);

    public Task<NoteEditorSession> CreateNewAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        return Task.FromResult(new NoteEditorSession(
            _notes,
            _saves,
            _drafts,
            new EditorSnapshot
            {
                Sequence = 0,
                NoteId = Guid.NewGuid(),
                DraftId = Guid.NewGuid(),
                IsNew = true,
                Title = null,
                Body = string.Empty,
                CategoryId = Category.UncategorizedId,
                TagIds = Array.Empty<Guid>(),
                IsPinned = false,
                DisplayIntent = NoteDisplayIntent.Visible,
                CreatedAtUtc = now,
                ModifiedAtUtc = now,
                BaseVersion = 0
            },
            _options,
            _timeProvider));
    }

    public async Task<NoteEditorSession?> OpenAsync(
        Guid noteId,
        CancellationToken cancellationToken = default)
    {
        var note = await _notes.GetAsync(noteId, cancellationToken).ConfigureAwait(false);
        if (note is null)
        {
            return null;
        }

        var tagIds = await _notes.GetTagIdsAsync(noteId, cancellationToken).ConfigureAwait(false);
        return new NoteEditorSession(
            _notes,
            _saves,
            _drafts,
            FromNote(note, tagIds),
            _options,
            _timeProvider);
    }

    public async Task<NoteEditorSession> OpenFromDraftAsync(
        NoteDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var note = await _notes.GetAsync(draft.NoteId, cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var tagIds = note is null
            ? draft.TagIds.ToArray()
            : await _notes.GetTagIdsAsync(draft.NoteId, cancellationToken).ConfigureAwait(false);
        var snapshot = new EditorSnapshot
        {
            Sequence = 1,
            NoteId = draft.NoteId,
            DraftId = draft.DraftId,
            IsNew = note is null,
            Title = draft.Title,
            Body = draft.Body,
            CategoryId = draft.CategoryId,
            TagIds = tagIds.ToArray(),
            IsPinned = draft.IsPinned,
            DisplayIntent = draft.DisplayIntent,
            ColorMode = draft.ColorMode,
            ColorHex = draft.ColorHex,
            FontSize = draft.FontSize,
            WindowLayout = draft.WindowLayout,
            CreatedAtUtc = note?.CreatedAtUtc ?? now,
            ModifiedAtUtc = draft.CapturedAtUtc,
            BaseVersion = note is null ? 0 : draft.BaseVersion
        };
        return new NoteEditorSession(
            _notes,
            _saves,
            _drafts,
            snapshot,
            _options,
            _timeProvider,
            startDirty: true,
            initialDraftState: DraftProtectionState.Protected);
    }

    private static EditorSnapshot FromNote(Note note, IReadOnlyList<Guid> tagIds) => new()
    {
        Sequence = 0,
        NoteId = note.Id,
        DraftId = Guid.NewGuid(),
        IsNew = false,
        Title = note.Title,
        Body = note.Body,
        CategoryId = note.CategoryId,
        TagIds = tagIds.ToArray(),
        IsPinned = note.IsPinned,
        DisplayIntent = note.DisplayIntent,
        ColorMode = note.ColorMode,
        ColorHex = note.ColorHex,
        FontSize = note.FontSize,
        WindowLayout = note.WindowLayout,
        CreatedAtUtc = note.CreatedAtUtc,
        ModifiedAtUtc = note.ModifiedAtUtc,
        BaseVersion = note.Version
    };
}
