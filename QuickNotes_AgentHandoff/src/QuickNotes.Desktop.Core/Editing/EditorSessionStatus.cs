namespace QuickNotes.Desktop.Editing;

public sealed record EditorSessionStatus
{
    public required Guid NoteId { get; init; }
    public required Guid DraftId { get; init; }
    public required bool IsNew { get; init; }
    public required EditorSaveState SaveState { get; init; }
    public required DraftProtectionState DraftState { get; init; }
    public required long Sequence { get; init; }
    public required long BaseVersion { get; init; }
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }
    public bool HasPendingChanges { get; init; }
}
