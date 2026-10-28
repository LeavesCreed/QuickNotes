namespace QuickNotes.Domain;

public sealed record TopBarConfig
{
    public static TopBarConfig Default { get; } = new()
    {
        Actions = new[] { TopBarAction.Pin, TopBarAction.More }
    };

    public required IReadOnlyList<TopBarAction> Actions { get; init; }

    public void Validate()
    {
        if (Actions is null
            || Actions.Count > 8
            || Actions.Distinct().Count() != Actions.Count
            || Actions.Any(action => !Enum.IsDefined(action)))
        {
            throw new ArgumentException("Top bar actions are invalid.", nameof(Actions));
        }
    }
}
