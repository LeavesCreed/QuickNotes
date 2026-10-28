using QuickNotes.Application;
using QuickNotes.Desktop.Paths;

namespace QuickNotes.Desktop.Editing;

public sealed class DraftRecoveryService
{
    private readonly DesktopPaths _paths;
    private readonly INoteRepository _notes;
    private readonly INoteDraftStore _drafts;
    private readonly EditorWorkspaceService _workspace;

    public DraftRecoveryService(
        DesktopPaths paths,
        INoteRepository notes,
        INoteDraftStore drafts,
        EditorWorkspaceService workspace)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _notes = notes ?? throw new ArgumentNullException(nameof(notes));
        _drafts = drafts ?? throw new ArgumentNullException(nameof(drafts));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    public async Task<DraftRecoveryInventory> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var items = new List<DraftRecoveryItem>();
        var issues = new List<DraftRecoveryIssue>();
        if (!Directory.Exists(_paths.DraftDirectory))
        {
            return new DraftRecoveryInventory(items, issues);
        }

        IReadOnlyList<string> files;
        try
        {
            files = Directory.GetFiles(_paths.DraftDirectory, "*.json").ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            issues.Add(new DraftRecoveryIssue(
                _paths.DraftDirectory,
                null,
                "draft_directory_unavailable",
                exception.GetType().Name));
            return new DraftRecoveryInventory(items, issues);
        }

        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(path);
            if (!Guid.TryParse(Path.GetFileNameWithoutExtension(path), out var draftId))
            {
                issues.Add(new DraftRecoveryIssue(
                    fileName,
                    null,
                    "draft_name_invalid",
                    "The draft file name does not contain a draft id."));
                continue;
            }

            NoteDraft? draft;
            try
            {
                draft = await _drafts.GetAsync(draftId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DraftStoreException exception)
            {
                issues.Add(new DraftRecoveryIssue(
                    fileName,
                    draftId,
                    exception.ErrorCode,
                    "The draft could not be read."));
                continue;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                issues.Add(new DraftRecoveryIssue(
                    fileName,
                    draftId,
                    "draft_io",
                    exception.GetType().Name));
                continue;
            }

            if (draft is null)
            {
                issues.Add(new DraftRecoveryIssue(
                    fileName,
                    draftId,
                    "draft_missing",
                    "The draft file disappeared while listing."));
                continue;
            }

            var exists = await _notes.GetAsync(draft.NoteId, cancellationToken).ConfigureAwait(false)
                is not null;
            items.Add(new DraftRecoveryItem(draft, exists));
        }

        return new DraftRecoveryInventory(
            items.OrderByDescending(item => item.Draft.CapturedAtUtc).ToArray(),
            issues);
    }

    public Task<NoteEditorSession> OpenAsync(
        NoteDraft draft,
        CancellationToken cancellationToken = default) =>
        _workspace.OpenFromDraftAsync(draft, cancellationToken);

    public Task DiscardAsync(Guid draftId, CancellationToken cancellationToken = default) =>
        _drafts.DeleteAsync(draftId, cancellationToken);
}
