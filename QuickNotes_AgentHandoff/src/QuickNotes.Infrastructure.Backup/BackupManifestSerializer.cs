using System.Text.Json;
using System.Text.Json.Serialization;
using QuickNotes.Application;

namespace QuickNotes.Infrastructure.Backup;

public static class BackupManifestSerializer
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static byte[] Serialize(BackupManifest manifest)
    {
        Validate(manifest);
        return JsonSerializer.SerializeToUtf8Bytes(manifest, Options);
    }

    public static BackupManifest Deserialize(ReadOnlySpan<byte> json)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<BackupManifest>(json, Options);
            if (manifest is null)
            {
                throw new BackupException(
                    "manifest",
                    BackupErrorCodes.ManifestInvalid,
                    "The backup manifest is empty.");
            }

            Validate(manifest);
            return manifest;
        }
        catch (BackupException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new BackupException(
                "manifest",
                BackupErrorCodes.ManifestInvalid,
                "The backup manifest is not valid JSON.",
                exception);
        }
        catch (ArgumentException exception)
        {
            throw new BackupException(
                "manifest",
                BackupErrorCodes.ManifestInvalid,
                "The backup manifest is invalid.",
                exception);
        }
    }

    public static void Validate(BackupManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (manifest.FormatVersion != BackupManifest.CurrentFormatVersion)
        {
            throw new ArgumentException(
                $"Unsupported manifest format version {manifest.FormatVersion}.",
                nameof(manifest));
        }

        if (!Enum.IsDefined(manifest.BackupType))
        {
            throw new ArgumentException(
                "BackupType must be a defined value.",
                nameof(manifest));
        }

        if (manifest.CreatedAtUtc == default || manifest.CreatedAtLocal == default)
        {
            throw new ArgumentException(
                "Manifest timestamps must be set.",
                nameof(manifest));
        }

        if (manifest.CreatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "CreatedAtUtc must use UTC.",
                nameof(manifest));
        }

        if (manifest.CreatedAtUtc.UtcDateTime != manifest.CreatedAtLocal.UtcDateTime)
        {
            throw new ArgumentException(
                "CreatedAtUtc and CreatedAtLocal must represent the same instant.",
                nameof(manifest));
        }

        if (manifest.SchemaVersion < 1)
        {
            throw new ArgumentException(
                "SchemaVersion must be at least 1.",
                nameof(manifest));
        }

        if (!string.Equals(
                manifest.DatabaseFileName,
                BackupManifest.DatabaseEntryName,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"DatabaseFileName must be {BackupManifest.DatabaseEntryName}.",
                nameof(manifest));
        }

        if (manifest.DatabaseSizeBytes <= 0)
        {
            throw new ArgumentException(
                "DatabaseSizeBytes must be positive.",
                nameof(manifest));
        }

        if (!IsLowerHexSha256(manifest.DatabaseSha256))
        {
            throw new ArgumentException(
                "DatabaseSha256 must be 64 lowercase hexadecimal characters.",
                nameof(manifest));
        }
    }

    private static bool IsLowerHexSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }
}
