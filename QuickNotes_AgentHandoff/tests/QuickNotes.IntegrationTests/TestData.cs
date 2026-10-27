using QuickNotes.Domain;

namespace QuickNotes.IntegrationTests;

internal static class TestData
{
    public static Note NewNote()
    {
        var timestamp = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        return new Note
        {
            Id = Guid.NewGuid(),
            Title = "title",
            Body = "body",
            CreatedAtUtc = timestamp,
            ModifiedAtUtc = timestamp,
            CategoryId = Category.UncategorizedId
        };
    }
}
