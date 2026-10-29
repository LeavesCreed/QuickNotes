namespace QuickNotes.Desktop.Windows;

public sealed record NoteWindowSyncPlan(
    IReadOnlyList<Guid> Create,
    IReadOnlyList<Guid> KeepOpen,
    IReadOnlyList<Guid> Close,
    IReadOnlyList<Guid> PendingNewNoteIds);

public static class NoteWindowSyncPlanner
{
    public static NoteWindowSyncPlan Plan(
        IReadOnlyCollection<Guid> desiredNoteIds,
        IReadOnlyCollection<Guid> openWindowIds,
        IReadOnlyCollection<Guid> pendingNewNoteIds,
        Func<Guid, bool> isStillNew)
    {
        var desired = desiredNoteIds.ToHashSet();
        var open = openWindowIds.ToHashSet();
        var pending = pendingNewNoteIds.ToHashSet();
        var keep = new List<Guid>();
        var close = new List<Guid>();
        foreach (var noteId in open)
        {
            if (desired.Contains(noteId))
            {
                keep.Add(noteId);
                pending.Remove(noteId);
            }
            else if (pending.Contains(noteId) && isStillNew(noteId))
            {
                keep.Add(noteId);
            }
            else
            {
                close.Add(noteId);
                pending.Remove(noteId);
            }
        }

        var create = desired.Where(noteId => !open.Contains(noteId)).ToArray();
        return new NoteWindowSyncPlan(
            create,
            keep,
            close,
            pending.ToArray());
    }
}
