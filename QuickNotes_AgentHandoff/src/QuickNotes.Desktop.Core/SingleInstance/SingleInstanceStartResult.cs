namespace QuickNotes.Desktop.SingleInstance;

public enum SingleInstanceStartResult
{
    Owner,
    ActivatedExisting,
    Unresponsive,
    Error
}
