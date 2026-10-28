namespace QuickNotes.Domain;

public sealed record HotkeySettings
{
    public static HotkeySettings Default { get; } = new()
    {
        NewNote = "Ctrl+Alt+N",
        OpenManagementCenter = "Ctrl+Alt+M",
        ToggleGlobalHidden = "Ctrl+Alt+H"
    };

    public string? NewNote { get; init; }
    public string? OpenManagementCenter { get; init; }
    public string? ToggleGlobalHidden { get; init; }

    public void Validate()
    {
        ValidateGesture(NewNote, nameof(NewNote));
        ValidateGesture(OpenManagementCenter, nameof(OpenManagementCenter));
        ValidateGesture(ToggleGlobalHidden, nameof(ToggleGlobalHidden));
    }

    private static void ValidateGesture(string? gesture, string propertyName)
    {
        if (gesture is not null && string.IsNullOrWhiteSpace(gesture))
        {
            throw new ArgumentException(
                "A hotkey gesture must be null (disabled) or a non-empty string.",
                propertyName);
        }
    }
}
