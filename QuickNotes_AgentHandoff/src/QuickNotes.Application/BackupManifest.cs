namespace QuickNotes.Application;

public sealed record BackupManifest
{
    public const int CurrentFormatVersion = 1;
    public const string ManifestEntryName = "manifest.json";
    public const string DatabaseEntryName = "notes.db";

    public required int FormatVersion { get; init; }
    public required BackupType BackupType { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset CreatedAtLocal { get; init; }
    public required long SchemaVersion { get; init; }
    public required string DatabaseFileName { get; init; }
    public required long DatabaseSizeBytes { get; init; }
    public required string DatabaseSha256 { get; init; }
}
