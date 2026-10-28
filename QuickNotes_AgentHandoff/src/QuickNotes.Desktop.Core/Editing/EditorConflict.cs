using QuickNotes.Domain;

namespace QuickNotes.Desktop.Editing;

public sealed record EditorConflict(
    EditorSnapshot LocalSnapshot,
    Note? RemoteNote,
    long? RemoteVersion);
