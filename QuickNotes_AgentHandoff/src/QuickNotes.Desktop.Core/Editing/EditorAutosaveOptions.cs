namespace QuickNotes.Desktop.Editing;

public sealed record EditorAutosaveOptions
{
    public TimeSpan DraftDebounce { get; init; } = TimeSpan.FromMilliseconds(300);
    public TimeSpan DraftMaxDelay { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan SaveDebounce { get; init; } = TimeSpan.FromMilliseconds(800);
    public TimeSpan SaveMaxDelay { get; init; } = TimeSpan.FromSeconds(5);
    public bool SaveOnFocusLoss { get; init; } = true;
    public bool SaveOnNoteSwitch { get; init; } = true;

    public void Validate()
    {
        if (DraftDebounce < TimeSpan.Zero
            || DraftMaxDelay <= TimeSpan.Zero
            || SaveDebounce < TimeSpan.Zero
            || SaveMaxDelay <= TimeSpan.Zero
            || SaveMaxDelay < SaveDebounce)
        {
            throw new ArgumentOutOfRangeException(
                nameof(EditorAutosaveOptions),
                "Autosave delays must be non-negative and max delays must bound debounce delays.");
        }
    }
}
