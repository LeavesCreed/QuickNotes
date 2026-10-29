using QuickNotes.Domain;

namespace QuickNotes.Application;

public enum NoteManagementItemStatus
{
    Succeeded,
    Conflict,
    NotFound,
    Failed
}

public sealed record NoteManagementItemResult(
    Guid NoteId,
    NoteManagementItemStatus Status,
    Note? Note,
    string? ErrorCode,
    string? Message);

public sealed record BatchOperationResult(
    IReadOnlyList<NoteManagementItemResult> Items)
{
    public bool AllSucceeded =>
        Items.Count > 0
        && Items.All(item => item.Status == NoteManagementItemStatus.Succeeded);

    public bool HasFailures =>
        Items.Any(item => item.Status != NoteManagementItemStatus.Succeeded);
}
