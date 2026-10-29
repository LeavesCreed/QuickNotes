using QuickNotes.Domain;

namespace QuickNotes.Desktop.Windows;

public static class GlobalDisplayState
{
    public static bool ShouldShowOnDesktop(Note note, bool globalHidden) =>
        !globalHidden
        && note.Lifecycle == NoteLifecycle.Active
        && note.DisplayIntent == NoteDisplayIntent.Visible;

    public static IReadOnlyList<Note> VisibleNotes(
        IEnumerable<Note> notes,
        bool globalHidden) =>
        globalHidden
            ? Array.Empty<Note>()
            : notes
                .Where(note => ShouldShowOnDesktop(note, globalHidden: false))
                .ToArray();
}
