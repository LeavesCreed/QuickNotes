namespace QuickNotes.Domain;

public sealed record Category
{
    public static readonly Guid UncategorizedId = Guid.Parse("6f2a98f2-20de-4f4e-a2f4-8a570c4625d7");
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string DefaultColor { get; init; }
    public bool IsSystem { get; init; }
}
