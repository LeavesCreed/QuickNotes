using QuickNotes.Domain;

namespace QuickNotes.Application;

public enum ManagementUndoKind
{
    Category,
    Tags,
    Lifecycle,
    Copy
}

public sealed record ManagementUndoSnapshot(
    Guid NoteId,
    long PriorVersion,
    NoteLifecycle PriorLifecycle,
    NoteDisplayIntent PriorDisplayIntent,
    Guid PriorCategoryId,
    IReadOnlyList<Guid> PriorTagIds,
    bool PriorIsPinned,
    NoteColorMode PriorColorMode,
    string? PriorColorHex,
    int? PriorFontSize,
    NoteWindowLayout? PriorWindowLayout,
    string? PriorTitle,
    string PriorBody);

public sealed record ManagementUndoToken(
    Guid TokenId,
    ManagementUndoKind Kind,
    IReadOnlyList<ManagementUndoSnapshot> Snapshots,
    string Description);

public enum ManagementUndoItemStatus
{
    Succeeded,
    Stale,
    NeedsConfirmation,
    NotFound,
    Failed
}

public sealed record ManagementUndoItemResult(
    Guid NoteId,
    ManagementUndoItemStatus Status,
    string? Message);

public sealed record ManagementUndoResult(
    IReadOnlyList<ManagementUndoItemResult> Items)
{
    public bool AllSucceeded =>
        Items.Count > 0
        && Items.All(item => item.Status == ManagementUndoItemStatus.Succeeded);
}
