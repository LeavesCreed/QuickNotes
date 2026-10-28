namespace QuickNotes.Desktop.State;

public enum ApplicationStateReadStatus
{
    Found,
    NotFound,
    Invalid
}

public sealed record ApplicationStateReadResult
{
    public required ApplicationStateReadStatus Status { get; init; }
    public ApplicationState? State { get; init; }
    public string? Error { get; init; }

    public static ApplicationStateReadResult Found(ApplicationState state) => new()
    {
        Status = ApplicationStateReadStatus.Found,
        State = state
    };

    public static ApplicationStateReadResult NotFound() => new()
    {
        Status = ApplicationStateReadStatus.NotFound
    };

    public static ApplicationStateReadResult Invalid(string error) => new()
    {
        Status = ApplicationStateReadStatus.Invalid,
        Error = error
    };
}
