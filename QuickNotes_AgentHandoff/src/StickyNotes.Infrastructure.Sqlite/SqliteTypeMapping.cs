using System.Globalization;
using Microsoft.Data.Sqlite;
using StickyNotes.Domain;

namespace StickyNotes.Infrastructure.Sqlite;

internal static class SqliteTypeMapping
{
    public static string GuidString(Guid value) => value.ToString("D");

    public static string UtcString(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public static Note ReadNote(SqliteDataReader reader)
    {
        return new Note
        {
            Id = Guid.Parse(reader.GetString(0)),
            Title = reader.IsDBNull(1) ? null : reader.GetString(1),
            Body = reader.GetString(2),
            CreatedAtUtc = ReadUtc(reader, 3),
            ModifiedAtUtc = ReadUtc(reader, 4),
            Version = reader.GetInt64(5),
            Lifecycle = (NoteLifecycle)reader.GetInt32(6),
            CategoryId = Guid.Parse(reader.GetString(7)),
            IsPinned = reader.GetInt64(8) != 0,
            DisplayIntent = (NoteDisplayIntent)reader.GetInt32(9)
        };
    }

    private static DateTimeOffset ReadUtc(SqliteDataReader reader, int ordinal) =>
        DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
