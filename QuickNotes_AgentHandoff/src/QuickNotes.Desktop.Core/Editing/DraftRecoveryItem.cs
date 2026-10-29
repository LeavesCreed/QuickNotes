using QuickNotes.Application;

namespace QuickNotes.Desktop.Editing;

public sealed record DraftRecoveryItem(
    NoteDraft Draft,
    bool NoteExists,
    bool IsPreRestore = false);

public sealed record DraftRecoveryIssue(
    string FileName,
    Guid? DraftId,
    string ErrorCode,
    string Message);

public sealed record DraftRecoveryInventory(
    IReadOnlyList<DraftRecoveryItem> Items,
    IReadOnlyList<DraftRecoveryIssue> Issues);
