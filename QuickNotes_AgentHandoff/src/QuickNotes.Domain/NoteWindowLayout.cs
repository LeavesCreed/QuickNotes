namespace QuickNotes.Domain;

public sealed record NoteWindowLayout
{
    public required double Left { get; init; }
    public required double Top { get; init; }
    public required double Width { get; init; }
    public required double Height { get; init; }
}
