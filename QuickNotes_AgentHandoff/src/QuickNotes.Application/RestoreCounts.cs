namespace QuickNotes.Application;

public sealed record RestoreCounts(
    long ActiveNotes,
    long ArchivedNotes,
    long TrashNotes,
    long Categories,
    long Tags,
    long NoteTags);
