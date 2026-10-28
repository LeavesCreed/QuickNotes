namespace QuickNotes.Desktop.Editing;

public enum EditorSaveState
{
    Clean,
    Modified,
    Drafting,
    Saving,
    Saved,
    SaveFailed,
    Conflict,
    OutcomeUnknown
}
