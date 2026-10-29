using QuickNotes.Desktop.Windows;

namespace QuickNotes.UnitTests;

public sealed class NoteWindowSyncPlannerTests
{
    [Fact]
    public void UnpersistedNewNoteWindow_IsKeptOpen()
    {
        var newId = Guid.NewGuid();
        var plan = NoteWindowSyncPlanner.Plan(
            desiredNoteIds: Array.Empty<Guid>(),
            openWindowIds: new[] { newId },
            pendingNewNoteIds: new[] { newId },
            isStillNew: _ => true);

        Assert.Contains(newId, plan.KeepOpen);
        Assert.DoesNotContain(newId, plan.Close);
        Assert.Empty(plan.Create);
    }

    [Fact]
    public void PersistedNewNoteWindow_TransitionsToNormalWindow()
    {
        var newId = Guid.NewGuid();
        var plan = NoteWindowSyncPlanner.Plan(
            desiredNoteIds: new[] { newId },
            openWindowIds: new[] { newId },
            pendingNewNoteIds: new[] { newId },
            isStillNew: _ => false);

        Assert.Contains(newId, plan.KeepOpen);
        Assert.Empty(plan.Close);
        Assert.Empty(plan.Create);
        Assert.Empty(plan.PendingNewNoteIds);
    }

    [Fact]
    public void PendingNewNote_ThatIsNoLongerNewAndNotDesired_IsClosed()
    {
        var noteId = Guid.NewGuid();
        var plan = NoteWindowSyncPlanner.Plan(
            desiredNoteIds: Array.Empty<Guid>(),
            openWindowIds: new[] { noteId },
            pendingNewNoteIds: new[] { noteId },
            isStillNew: _ => false);

        Assert.Contains(noteId, plan.Close);
        Assert.DoesNotContain(noteId, plan.KeepOpen);
        Assert.Empty(plan.PendingNewNoteIds);
    }

    [Fact]
    public void DesiredMissingWindow_IsCreated()
    {
        var noteId = Guid.NewGuid();
        var plan = NoteWindowSyncPlanner.Plan(
            desiredNoteIds: new[] { noteId },
            openWindowIds: Array.Empty<Guid>(),
            pendingNewNoteIds: Array.Empty<Guid>(),
            isStillNew: _ => false);

        Assert.Contains(noteId, plan.Create);
    }
}
