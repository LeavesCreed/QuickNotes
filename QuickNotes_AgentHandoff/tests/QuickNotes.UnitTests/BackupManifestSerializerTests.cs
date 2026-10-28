using System.Text;
using System.Text.Json.Nodes;
using QuickNotes.Application;
using QuickNotes.Infrastructure.Backup;
using Xunit;

namespace QuickNotes.UnitTests;

public sealed class BackupManifestSerializerTests
{
    [Fact]
    public void SerializeAndDeserialize_RoundTripsAndUsesStableJsonShape()
    {
        var manifest = NewManifest();

        var json = BackupManifestSerializer.Serialize(manifest);
        var loaded = BackupManifestSerializer.Deserialize(json);

        Assert.Equal(manifest, loaded);
        var text = Encoding.UTF8.GetString(json);
        Assert.Contains("\"formatVersion\": 1", text);
        Assert.Contains("\"backupType\": \"Manual\"", text);
        Assert.DoesNotContain("BackupType", text);
        Assert.False(json.Length >= 3 && json[0] == 0xEF && json[1] == 0xBB && json[2] == 0xBF);
    }

    [Fact]
    public void Deserialize_UnknownFormatVersion_ThrowsManifestInvalid()
    {
        var json = Encoding.UTF8.GetString(BackupManifestSerializer.Serialize(NewManifest()))
            .Replace("\"formatVersion\": 1", "\"formatVersion\": 2", StringComparison.Ordinal);

        var exception = Assert.Throws<BackupException>(
            () => BackupManifestSerializer.Deserialize(Encoding.UTF8.GetBytes(json)));

        Assert.Equal(BackupErrorCodes.ManifestInvalid, exception.ErrorCode);
    }

    [Fact]
    public void Deserialize_MissingRequiredField_ThrowsManifestInvalid()
    {
        var node = JsonNode.Parse(BackupManifestSerializer.Serialize(NewManifest()))!.AsObject();
        node.Remove("schemaVersion");

        var exception = Assert.Throws<BackupException>(
            () => BackupManifestSerializer.Deserialize(Encoding.UTF8.GetBytes(node.ToJsonString())));

        Assert.Equal(BackupErrorCodes.ManifestInvalid, exception.ErrorCode);
    }

    [Fact]
    public void Deserialize_NumericEnum_ThrowsManifestInvalid()
    {
        var json = Encoding.UTF8.GetString(BackupManifestSerializer.Serialize(NewManifest()))
            .Replace("\"backupType\": \"Manual\"", "\"backupType\": 0", StringComparison.Ordinal);

        var exception = Assert.Throws<BackupException>(
            () => BackupManifestSerializer.Deserialize(Encoding.UTF8.GetBytes(json)));

        Assert.Equal(BackupErrorCodes.ManifestInvalid, exception.ErrorCode);
    }

    [Fact]
    public void Deserialize_InvalidHash_ThrowsManifestInvalid()
    {
        var json = Encoding.UTF8.GetString(BackupManifestSerializer.Serialize(NewManifest()))
            .Replace(new string('a', 64), new string('A', 64), StringComparison.Ordinal);

        var exception = Assert.Throws<BackupException>(
            () => BackupManifestSerializer.Deserialize(Encoding.UTF8.GetBytes(json)));

        Assert.Equal(BackupErrorCodes.ManifestInvalid, exception.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidManifests_Throw()
    {
        var invalidManifests = new[]
        {
            NewManifest() with { FormatVersion = 2 },
            NewManifest() with { BackupType = (BackupType)42 },
            NewManifest() with { CreatedAtUtc = default },
            NewManifest() with { CreatedAtUtc = new DateTimeOffset(2026, 10, 28, 9, 30, 0, TimeSpan.FromHours(8)) },
            NewManifest() with { CreatedAtLocal = NewManifest().CreatedAtUtc.AddHours(1) },
            NewManifest() with { SchemaVersion = 0 },
            NewManifest() with { DatabaseFileName = "other.db" },
            NewManifest() with { DatabaseSizeBytes = 0 },
            NewManifest() with { DatabaseSha256 = new string('a', 63) },
            NewManifest() with { DatabaseSha256 = new string('z', 64) }
        };

        foreach (var manifest in invalidManifests)
        {
            Assert.Throws<ArgumentException>(() => BackupManifestSerializer.Validate(manifest));
            Assert.Throws<ArgumentException>(() => BackupManifestSerializer.Serialize(manifest));
        }
    }

    private static BackupManifest NewManifest() => new()
    {
        FormatVersion = BackupManifest.CurrentFormatVersion,
        BackupType = BackupType.Manual,
        CreatedAtUtc = new DateTimeOffset(2026, 10, 28, 9, 30, 0, TimeSpan.Zero),
        CreatedAtLocal = new DateTimeOffset(2026, 10, 28, 17, 30, 0, TimeSpan.FromHours(8)),
        SchemaVersion = 1,
        DatabaseFileName = BackupManifest.DatabaseEntryName,
        DatabaseSizeBytes = 4096,
        DatabaseSha256 = new string('a', 64)
    };
}
