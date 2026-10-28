using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using QuickNotes.Domain;

namespace QuickNotes.Infrastructure.Sqlite;

internal static class SqliteTypeMapping
{
    private static readonly JsonSerializerOptions TopBarJsonOptions = CreateTopBarJsonOptions();

    public static string GuidString(Guid value) => value.ToString("D");

    public static string UtcString(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public static DateTimeOffset ReadUtc(SqliteDataReader reader, int ordinal) =>
        DateTimeOffset.Parse(
            reader.GetString(ordinal),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

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
            DisplayIntent = (NoteDisplayIntent)reader.GetInt32(9),
            ColorMode = (NoteColorMode)reader.GetInt32(10),
            ColorHex = reader.IsDBNull(11) ? null : reader.GetString(11),
            FontSize = reader.IsDBNull(12) ? null : reader.GetInt32(12),
            WindowLayout = reader.IsDBNull(13)
                ? null
                : new NoteWindowLayout
                {
                    Left = reader.GetDouble(13),
                    Top = reader.GetDouble(14),
                    Width = reader.GetDouble(15),
                    Height = reader.GetDouble(16)
                }
        };
    }

    public static string SerializeTopBarConfig(TopBarConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Validate();
        return JsonSerializer.Serialize(config.Actions, TopBarJsonOptions);
    }

    public static TopBarConfig DeserializeTopBarConfig(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("The top bar configuration is empty.");
        }

        try
        {
            var actions = JsonSerializer.Deserialize<TopBarAction[]>(json, TopBarJsonOptions)
                ?? throw new InvalidOperationException("The top bar configuration is empty.");
            var config = new TopBarConfig { Actions = actions };
            config.Validate();
            return config;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "The top bar configuration JSON is invalid.",
                exception);
        }
    }

    private static JsonSerializerOptions CreateTopBarJsonOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }
}
