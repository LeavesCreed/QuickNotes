using QuickNotes.Application;
using QuickNotes.Desktop.Editing;

namespace QuickNotes.Desktop.Hosting;

public sealed record DesktopEditingServices(
    INoteRepository Notes,
    INoteDraftStore Drafts,
    INoteSaveCoordinator Saves,
    EditorWorkspaceService Workspace,
    DraftRecoveryService DraftRecovery,
    ICatalogRepository Catalog,
    NoteManagementService Management,
    ManagementUndoService Undo,
    IApplicationSettingsRepository Settings,
    EditorSessionRegistry Sessions);
